#region Copyright 
// Copyright 2017 Gigya Inc.  All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License"); 
// you may not use this file except in compliance with the License.  
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDER AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Runtime.Caching;
using System.Runtime.Caching.Hosting;
using System.Threading;
using System.Threading.Tasks;
using Gigya.Microdot.Interfaces.Logging;
using Gigya.Microdot.Interfaces.SystemWrappers;
using Gigya.ServiceContract.HttpService;
using Metrics;
using System.Threading.Tasks.Dataflow;

// ReSharper disable InconsistentlySynchronizedField

namespace Gigya.Microdot.ServiceProxy.Caching
{
    public sealed class AsyncCache : IMemoryCacheManager, IServiceProvider, IDisposable
    {
        private class Statistics
        {
            private const double Mb = 1048576;
            private MetricsContext Metrics { get; }
            public MetricsContext Hits { get; }
            public MetricsContext Misses { get; }
            public MetricsContext JoinedTeam { get; }
            public MetricsContext AwaitingResult { get; }
            public MetricsContext Failed { get; }
            public Counter ClearCache { get; }
            public MetricsContext Items { get; }
            public MetricsContext Revokes { get; }

            public Statistics(AsyncCache subject, MetricsContext metrics)
            {
                // Gauges
                Metrics = metrics;
                Metrics.Gauge("Size", () => subject.LastCacheSizeBytes / Mb, Unit.MegaBytes);
                Metrics.Gauge("Entries", () => subject.MemoryCache.GetCount(), Unit.Items);
                Metrics.Gauge("SizeLimit", () => subject.MemoryCache.CacheMemoryLimit / Mb, Unit.MegaBytes);
                Metrics.Gauge("RamUsageLimit", () => subject.MemoryCache.PhysicalMemoryLimit, Unit.Percent);

                // Counters
                AwaitingResult = Metrics.Context("AwaitingResult");
                ClearCache = Metrics.Counter("ClearCache", Unit.Calls);

                // Meters
                Hits = Metrics.Context("Hits");
                Misses = Metrics.Context("Misses");
                JoinedTeam = Metrics.Context("JoinedTeam");
                Failed = Metrics.Context("Failed");

                Items = Metrics.Context("Items");
                Revokes = Metrics.Context("Revoke");
            }
        }
        private IDateTime DateTime { get; }
        private Func<CacheConfig> GetRevokeConfig { get; }
        private ILog Log { get; }
        private MemoryCache MemoryCache { get; set; }
        private long LastCacheSizeBytes { get; set; }

        private readonly Statistics _stats;

        private readonly IRevokeQueueMaintainer _revokesMaintainer;
        internal ConcurrentDictionary<string, ReverseItem> RevokeKeyToCacheKeysIndex { get; set; } = new ConcurrentDictionary<string, ReverseItem>();

        private IDisposable RevokeDisposable { get; }

        private int _clearCount;

        /// <summary>
        /// Not thread safe used for testing
        /// </summary>
        internal int CacheKeyCount => RevokeKeyToCacheKeysIndex.Sum(item => item.Value.CacheKeysSet.Count);

        public AsyncCache(ILog log, MetricsContext metrics, IDateTime dateTime, IRevokeListener revokeListener, Func<CacheConfig> getRevokeConfig, Func<ConcurrentDictionary<string, ReverseItem>, IRevokeQueueMaintainer> funcRevokesMaintainer)
        {
            // Clean up queue of revokes periodically
            _revokesMaintainer = funcRevokesMaintainer(RevokeKeyToCacheKeysIndex);

            DateTime = dateTime;
            GetRevokeConfig = getRevokeConfig;
            _stats = new Statistics(this, metrics);
            Log = log;

            if (ObjectCache.Host == null)
                ObjectCache.Host = this;
            else
                Log.Error("AsyncCache: Failed to register with ObjectCache.Host since it was already registered. Cache memory size will not be reported to Metrics.NET. Please make sure AsyncCache is singleton to prevent this issue.");

            Clear();

           var onRevoke = new ActionBlock<string>(OnRevoke);
            RevokeDisposable = revokeListener.RevokeSource.LinkTo(onRevoke);
        }

        private Task OnRevoke(string revokeKey)
        {
            var shouldLog = GetRevokeConfig().LogRevokes;

            if (string.IsNullOrEmpty(revokeKey))
            {
                Log.Warn("Error while revoking cache, revokeKey can't be null or empty");
                return Task.FromResult(false);
            }

            try
            {    
                if (shouldLog)
                    Log.Info(x => x("Revoke request received", unencryptedTags: new {revokeKey}));

                // We need to be sure to revoke item, if compete on TryGetValue of Reverse index
                var now = DateTime.UtcNow;

                var rItem = RevokeKeyToCacheKeysIndex.GetOrAdd(revokeKey, k =>
                {
                    var ri = new ReverseItem { WhenRevoked = now };

                    // we need to handle race between AlreadyRevoked GetOrAdd and Enqueue
                    _revokesMaintainer.Enqueue(revokeKey, now);

                    if (shouldLog)
                        Log.Info(x => x("RevokeKey is ahead of call.", unencryptedTags: new { revokeKey }));
                    return ri;
                });

                lock (rItem)
                {
                    rItem.WhenRevoked = now;
                    
                    // we have to, else call back on remove is changing CacheKeysSet
                    var cacheKeys = rItem.CacheKeysSet.ToArray();

                    if (shouldLog && cacheKeys.Length == 0)
                        Log.Info(x => x("There is no CacheKey to Revoke", unencryptedTags: new { revokeKey }));
                
                    foreach (var cacheKey in cacheKeys)
                    {
                        if (shouldLog)
                            Log.Info(x => x("Revoking cacheKey", unencryptedTags: new { revokeKey, cacheKey }));
                
                        MemoryCache.Remove(cacheKey);                      
                    }
                }
                _stats.Revokes.Meter("Succeeded", Unit.Events).Mark();
            }
            catch (Exception ex)
            {
                _stats.Revokes.Meter("Failed", Unit.Events).Mark();
                Log.Warn("Error while revoking cache", exception: ex, unencryptedTags: new {revokeKey});
            }
            return Task.FromResult(true);
        }

        public Task GetOrAdd(string key, Func<Task> factory, Type taskResultType, CacheItemPolicyEx policy, string groupName, string logData, string[] metricsKeys)
        {
            var getValueTask = GetOrAdd(key, () => TaskConverter.ToWeaklyTypedTask(factory(), taskResultType), policy, groupName, logData, metricsKeys, taskResultType);
            return TaskConverter.ToStronglyTypedTask(getValueTask, taskResultType);
        }

        private Task<object> GetOrAdd(string key, Func<Task<object>> factory, CacheItemPolicyEx policy, string groupName, string logData, string[] metricsKeys, Type taskResultType)
        {
            var shouldLog = ShouldLog(groupName);

            async Task<object> WrappedFactory(AsyncCacheItem item, bool removeOnException)
            {
                try
                {
                    if (shouldLog)
                        Log.Info(x => x("Cache item is waiting for value to be resolved", unencryptedTags: new
                        {
                            cacheKey = key,
                            cacheGroup = groupName,
                            cacheData = logData
                        }));

                    var result = await factory().ConfigureAwait(false);
                    if (shouldLog)
                    {
                        Log.Info(x => x("Cache item value is resolved", unencryptedTags: new
                        {
                            cacheKey = key,
                            cacheGroup = groupName,
                            cacheData = logData,
                            value = GetValueForLogging(result)
                        }));
                    }

                    var revokeKeys = (result as IRevocable)?.RevokeKeys?.ToArray();
                    // Could happen the item evicted from cache by a policy
                    if(revokeKeys != null && MemoryCache.Contains(key))
                    {
                        // Add items in reverse index for revoke keys
                        foreach (var revokeKey in revokeKeys)
                        {
                            var reverseEntry = RevokeKeyToCacheKeysIndex.GetOrAdd(revokeKey, k => new ReverseItem());

                            lock (reverseEntry)
                                reverseEntry.CacheKeysSet.Add(key);

                            Log.Info(x => x("RevokeKey added to reverse index", unencryptedTags: new
                            {
                                revokeKey,
                                cacheKey = key,
                                cacheGroup = groupName,
                                cacheData = logData
                            }));
                        }

                        AlreadyRevoked(item, revokeKeys, shouldLog, groupName, logData);
                    }

                    _stats.AwaitingResult.Decrement(metricsKeys);
                    return result;
                }
                catch(Exception exception)
                {
                    Log.Info(x => x("Error resolving value for cache item", unencryptedTags: new
                    {
                        cacheKey = key,
                        cacheGroup = groupName,
                        cacheData = logData,
                        removeOnException,
                        errorMessage = exception.Message
                    }));

                    if(removeOnException)
                        MemoryCache.Remove(key); // Do not cache exceptions.

                    _stats.AwaitingResult.Decrement(metricsKeys);
                    _stats.Failed.Mark(metricsKeys);
                    throw;
                }
            }

            var newItem = shouldLog == false
                ? new AsyncCacheItem () // if log is not needed, then do not cache unnecessary details which will blow up the memory
                : new AsyncCacheItem { GroupName = string.Intern(groupName), LogData = logData };

            Task<object> resultTask;

            // Taking a lock on the newItem in case it actually becomes the item in the cache (if no item with that key
            // existed). For another thread, it will be returned into the existingItem variable and will block on the
            // second lock, preventing concurrent mutation of the same object.
            lock (newItem.Lock)
            {
                if (typeof(IRevocable).IsAssignableFrom(taskResultType))
                    policy.RemovedCallback += ItemRemovedCallback;
                
                // Null is returned when the item doesn't exist in the cache!
                var existingItem = (AsyncCacheItem)MemoryCache.AddOrGetExisting(key, newItem, policy);

                if (existingItem == null)
                {
                    _stats.Misses.Mark(metricsKeys);
                    _stats.AwaitingResult.Increment(metricsKeys);
                    newItem.WhenCalled = DateTime.UtcNow;
                    newItem.CurrentValueTask = WrappedFactory(newItem, true);
                    newItem.NextRefreshTime = DateTime.UtcNow + policy.RefreshTime;
                    resultTask = newItem.CurrentValueTask;
                    if (shouldLog)
                        Log.Info(x => x("Item added to cache", unencryptedTags: new
                        {
                            cacheKey = key,
                            cacheGroup = groupName,
                            cacheData = logData
                        }));
                }
                else
                {
                    // This lock makes sure we're not mutating the same object as was added to the cache by an earlier
                    // thread (which was the first to add from 'newItem', for subsequent threads it will be 'existingItem').
                    lock (existingItem.Lock)
                    {
                        resultTask = existingItem.CurrentValueTask;

                        // Start refresh, if an existing refresh isn't in progress and we've passed the next refresh time.
                        if (existingItem.CurrentValueTask.IsCompleted &&
                            existingItem.RefreshTask?.IsCompleted != false && DateTime.UtcNow >= existingItem.NextRefreshTime)
                        {
                            existingItem.RefreshTask = ((Func<Task>)(async () =>
                                {
                                    try
                                    {
                                        existingItem.WhenCalled = DateTime.UtcNow;
                                        var getNewValue = WrappedFactory(existingItem, false);
                                        await getNewValue.ConfigureAwait(false);
                                        existingItem.CurrentValueTask = getNewValue;
                                        existingItem.NextRefreshTime = DateTime.UtcNow + policy.RefreshTime;
                                        if(!existingItem.Revoked)
                                            MemoryCache.Set(new CacheItem(key, existingItem), policy);
                                    }
                                    catch
                                    {
                                        existingItem.NextRefreshTime = DateTime.UtcNow + policy.FailedRefreshDelay;
                                    }
                                })).Invoke();
                        }
                    }

                    if (resultTask.GetAwaiter().IsCompleted)
                        _stats.Hits.Mark(metricsKeys);
                    else
                        _stats.JoinedTeam.Mark(metricsKeys);
                }
            }

            return resultTask;
        }

        /// <summary>
        /// Handle the case the revoke received in the middle of call to data source so the cacheItem should be removed.
        /// If won't be removed the cache item contains stall value unless evicted by policy, meaning "wrong" value returned.
        /// </summary>
        private void AlreadyRevoked(AsyncCacheItem cacheItem, IEnumerable<string> revokeKeys, bool shouldLog, string groupName, string logData)
        {
            foreach (var revokeKey in revokeKeys)
            {
                // If revoked, the reverse item will be in the index either GetOrAdd called or not!
                if (!RevokeKeyToCacheKeysIndex.TryGetValue(revokeKey, out ReverseItem reverseItem)) 
                    continue;

                // The cached item should be removed as revoke received after calling to data source
                if (reverseItem.WhenRevoked < cacheItem.WhenCalled)
                    continue;

                cacheItem.Revoked = true;

                string[] cacheKeys;

                // Race with OnRevoke, avoid possible modification exception
                lock (reverseItem)
                    cacheKeys = reverseItem.CacheKeysSet.ToArray();
                
                foreach (var cacheKey in cacheKeys)
                {
                    // Null returned if not found, will cause to Callback to clean reverse index
                    var isRemoved = MemoryCache.Remove(cacheKey) != null;

                    if (shouldLog)
                    {
                        var removed = isRemoved; // changing closure in loop
                        Log.Info(x => x("Removing cacheKey (revoked before call)", unencryptedTags: new
                        {
                            cacheKey,
                            cacheGroup = groupName,
                            cacheData = logData,
                            removed,
                            revokeKey,
                            diff = cacheItem.WhenCalled - reverseItem.WhenRevoked
                        }));
                    }
                }
            }
        }

        private ConcurrentDictionary<Type, FieldInfo> _revocableValueFieldPerType = new ConcurrentDictionary<Type, FieldInfo>();
        private string GetValueForLogging(object value)
        {
            if (value is IRevocable)
            {
                var revocableValueField = _revocableValueFieldPerType.GetOrAdd(value.GetType(), t=>t.GetField(nameof(Revocable<int>.Value)));
                if (revocableValueField != null)
                    value = revocableValueField.GetValue(value);
            }

            if (value is ValueType || value is string)
                return value.ToString();
            else
                return null;
        }

        /// <summary>
        /// For revocable items , move over all revoke ids in cache index and remove them.
        /// </summary>        
        private void ItemRemovedCallback(CacheEntryRemovedArguments arguments)
        {
            var cacheItem = arguments.CacheItem.Value as AsyncCacheItem;
            var shouldLog = ShouldLog(cacheItem?.GroupName);

            if (shouldLog)
                Log.Info(x => x("Item removed from cache", unencryptedTags: new
                {
                    cacheKey     = arguments.CacheItem.Key,
                    removeReason = arguments.RemovedReason.ToString(),
                    cacheGroup   = cacheItem?.GroupName,
                    cacheData    = cacheItem?.LogData
                })); 

            // will be null, if new and revoked in ahead
            var cachedItem = ((AsyncCacheItem)arguments.CacheItem.Value).CurrentValueTask;

            if(cachedItem?.Status == TaskStatus.RanToCompletion && (cachedItem.Result as IRevocable)?.RevokeKeys!=null)
            {
                foreach(var revokeKey in ((IRevocable)cachedItem.Result).RevokeKeys)
                {
                    if (RevokeKeyToCacheKeysIndex.TryGetValue(revokeKey, out ReverseItem reverseItem))
                    {
                        lock (reverseItem)
                        {
                            var cacheKeys = reverseItem.CacheKeysSet;
                            cacheKeys.Remove(arguments.CacheItem.Key);
                            Log.Info(x => x("CacheKey removed from reverse index", unencryptedTags: new
                            {
                                cacheKey = arguments.CacheItem.Key,
                                revokeKey,
                                removeReason = arguments.RemovedReason.ToString(),
                                cacheGroup = cacheItem?.GroupName,
                                cacheData = cacheItem?.LogData
                            }));

                            if (!cacheKeys.Any())
                            {
                                if (RevokeKeyToCacheKeysIndex.TryRemove(revokeKey, out _) && shouldLog)
                                    Log.Info(x => x("Reverse index for cache item was removed", unencryptedTags: new
                                    {
                                        cacheKey = arguments.CacheItem.Key,                                        
                                        removeReason = arguments.RemovedReason.ToString(),
                                        cacheGroup = cacheItem?.GroupName,
                                        cacheData = cacheItem?.LogData
                                    }));

                            }
                        }
                    }
                }
            }

            _stats.Items.Meter(arguments.RemovedReason.ToString(), Unit.Items).Mark();
        }

        private bool ShouldLog(string groupName)
        {
            if (groupName == null)
                return false;

            var config = GetRevokeConfig();
            if (config.Groups.TryGetValue(groupName, out var groupConfig))
                return groupConfig.WriteExtraLogs;

            return false;
        }


        public void Clear()
        {
            var oldMemoryCache = MemoryCache;
            MemoryCache = new MemoryCache(nameof(AsyncCache) + Interlocked.Increment(ref _clearCount), new NameValueCollection { { "PollingInterval", "00:00:01" } });

            if (oldMemoryCache != null)
            {
                // Disposing of MemoryCache can be a CPU intensive task and should therefore not block the current thread.
                // Triggering callback to run and clean up the reverse index
                Task.Run(() => oldMemoryCache.Dispose());
                _stats.ClearCache.Increment();
            }
        }


        public void UpdateCacheSize(long size, MemoryCache cache)
        {
            if (cache != MemoryCache)
                return;

            LastCacheSizeBytes = size;
        }


        public void ReleaseCache(MemoryCache cache) { }


        public object GetService(Type serviceType)
        {
            return serviceType == typeof(IMemoryCacheManager) ? this : null;
        }


        public void Dispose()
        {
            _revokesMaintainer.Dispose();
            MemoryCache?.Dispose();
            RevokeDisposable?.Dispose();
        }
    }
}
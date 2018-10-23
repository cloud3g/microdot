using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Gigya.Microdot.Fakes;
using Gigya.Microdot.Interfaces;
using Gigya.Microdot.Interfaces.SystemWrappers;
using Gigya.Microdot.ServiceDiscovery.Config;
using Gigya.Microdot.ServiceProxy;
using Gigya.Microdot.ServiceProxy.Caching;
using Gigya.Microdot.Testing.Shared;
using Gigya.Microdot.Testing.Shared.Utils;
using Gigya.ServiceContract.HttpService;
using Ninject;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Gigya.Microdot.UnitTests.Caching
{
    [TestFixture]
    public class CachingProxyTestsRevokeRace
    {
        const string FirstResult  = "First Result";
        const string SecondResult = "Second Result";

        private Dictionary<string, string> _configDic;
        private TestingKernel<ConsoleLog> _kernel;

        private Func<bool> _isFakeTime;
        private DateTime _now;

        private string _serviceResult;
        private ManualResetEvent _revokeSent = new ManualResetEvent(true);
        private ManualResetEvent _inMiddleOf = new ManualResetEvent(true);

        private ICachingTestService _proxy;
        private ICacheRevoker _cacheRevoker;
        private IRevokeListener _revokeListener;
        private ICachingTestService _serviceMock;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            // State making issues with reconfiguration.
        }

        [SetUp]
        public void Setup()
        {
            // State making issues with reconfiguration.
        }

        [TearDown]
        public void TearDown()
        {
            _kernel.Get<AsyncCache>().Clear();
        }

        private int _slowDownCallMs = 100;

        private void SetupServiceMock()
        {             
            _serviceMock = Substitute.For<ICachingTestService>();
            _serviceMock.CallService().Returns(_ => Task.FromResult(_serviceResult));
            _serviceMock.CallRevocableService(Arg.Any<string>()).Returns(async s =>
            {
                var result = _serviceResult;

                // Signal we in the middle of function
                _inMiddleOf.Set();


                // Race condition "point" between Revoke and AddGet (caching of value)
                // It will await for revoke request in progress
                _revokeSent.WaitOne();

                // Intro a delay to compete with periodic clean up of revokes
                await Task.Delay(_slowDownCallMs);

                return new Revocable<string>
                {
                    Value = result,
                    RevokeKeys = new[] {s.Args()[0].ToString()}
                };
            });
        
            _serviceResult = FirstResult;
            var serviceProxyMock = Substitute.For<IServiceProxyProvider<ICachingTestService>>();
            serviceProxyMock.Client.Returns(_serviceMock);
            _kernel.Rebind<IServiceProxyProvider<ICachingTestService>>().ToConstant(serviceProxyMock);
        }

        /*
                       
                               AddOrGet
                           (service called)

                                  |
                                  |                                 Task.Run()
           CallRevocableService   |                                    |
         +----------------------> 1)                                   |
                                  |                                    |                                
                                  |_inMiddleOf.Set();                  |                                     
                                  2)----------------------------------->                                
                                  |                                    |                                
                                  |                           _inMiddleOf.WaitOne();                    
                                  |                                    |   _cacheRevoker.Revoke(key);  |
                                  |                                    3------------------------------->
                                  |                                    |                           OnRevoke()
                                  |                            4)await eventWaiter;                    |
                                  |                                    |                               |
                         _revokeSent.WaitOne();                 5)revokeSent.Set()                     |
                                  |                                    |                               |
                                  6)<----------------------------------|                               |
                                  |                                    |                               |
                         (Slow down for few ms)                        |                               |
                                  |                                    |                               |
        <-------------------------+


        */

        [Test]
        public async Task RevokeQueue_ShouldBeCleanedPeriodically()
        {
            // Init service
            // Configure Revokes queue to be cleaned every 5 ms (an X value).
            // Call to revokable service, with revoke with an expected revoke in the middle.
            // Issue a revoke (such it will be in the middle of call)
            // Revoke expecting to enter to the queue
            // Stuck for more tha  X value to ensure it should be cleaned.
            // Because queue cleaned, the stall value will be returned instead expected new one.
            
            // We have to use actual DateTime and not a mock returning a constant/frozen value
            _isFakeTime = ()=> false;

            string x = (_slowDownCallMs / 2).ToString();
            _configDic = new Dictionary<string, string>
            {
                ["Cache.RevokesCleanupMilliseconds"] = x
            };

            _kernel = new TestingKernel<ConsoleLog>(mockConfig: _configDic);
            _kernel.Rebind(typeof(CachingProxyProvider<>)).ToSelf().InTransientScope();
            _kernel.Rebind<ICacheRevoker, IRevokeListener>().ToConstant(new FakeRevokingManager());

            SetupServiceMock();
            SetupDateTime();

            _proxy = _kernel.Get<ICachingTestService>();
            _cacheRevoker = _kernel.Get<ICacheRevoker>();
            _revokeListener = _kernel.Get<IRevokeListener>();


            // Configure to ensure clean up revoked cleaned before, and can't delete stall value
            var cacheConfig = _kernel.Get<Func<CacheConfig>>()();
            cacheConfig.RevokesCleanupMilliseconds.ToString().ShouldBe(x);

            var key = Guid.NewGuid().ToString();

            _serviceResult = FirstResult;

            // Simulate race between Revoke and AddGet
            _revokeSent = new ManualResetEvent(false);
            _inMiddleOf = new ManualResetEvent(false);

            Task.WaitAll(

                // Call to service to cache FirstResult (and stuck until _revokeDelay signaled)
                Task.Run(async () =>
                {
                    var result = await _proxy.CallRevocableService(key);
                    result.Value.ShouldBe(FirstResult, "Result should have been cached");
                }),

                // Revoke the key (not truly, as value is not actually cached, yet).
                Task.Run(async() =>
                {
                    _inMiddleOf.WaitOne();
                    var eventWaiter = _revokeListener.RevokeSource.WhenEventReceived(TimeSpan.FromMinutes(1));
                    await _cacheRevoker.Revoke(key);
                    await eventWaiter;     // Wait the revoke will be processed
                    _revokeSent.Set();     // Signal to continue adding/getting
                })
            );

            // Init return value (expect to be returned)
            _serviceResult = SecondResult;

            // As revokes was cleaned, we are getting FirstResult (stall value), instead second one would have expected
            await ResultRevocableShouldBe(FirstResult, key, "Expecting previous call value");
        }

        [Test]
        public async Task RevokeBeforeServiceResultReceived_ShouldRevokeStaleValue()
        {
            _configDic = new Dictionary<string,string>();
            _kernel = new TestingKernel<ConsoleLog>(mockConfig: _configDic);

            _kernel.Rebind(typeof(CachingProxyProvider<>)).ToSelf().InTransientScope();
            _kernel.Rebind<ICacheRevoker, IRevokeListener>().ToConstant(new FakeRevokingManager());

            SetupServiceMock();
            SetupDateTime();

            _proxy = _kernel.Get<ICachingTestService>();
            _cacheRevoker = _kernel.Get<ICacheRevoker>();
            _revokeListener = _kernel.Get<IRevokeListener>();

            // We have to use actual DateTime and not a mock returning a constant/frozen value
            _isFakeTime = ()=> false;

            var key = Guid.NewGuid().ToString();
            await ClearCachingPolicyConfig();

            // Init return value explicitly
            _serviceResult = FirstResult;

            // Simulate race between revoke and AddGet
            _revokeSent = new ManualResetEvent(false);
            _inMiddleOf = new ManualResetEvent(false);

            Task.WaitAll(

                // Call to service to cache FirstResult (and stuck until _revokeDelay signaled)
                Task.Run(async () =>
                {
                    var result = await _proxy.CallRevocableService(key);
                    result.Value.ShouldBe(FirstResult, "Result should have been cached");
                }),

                // Revoke the key (not truly, as value is not actually cached, yet).
                Task.Run(async() =>
                {
                    _inMiddleOf.WaitOne();
                    var eventWaiter = _revokeListener.RevokeSource.WhenEventReceived(TimeSpan.FromMinutes(1));
                    await _cacheRevoker.Revoke(key);
                    await eventWaiter;     // Wait the revoke will be processed
                    _revokeSent.Set();     // Signal to continue adding/getting
                })
            );

            // Init return value and expect to be returned, if not cached the first one!
            _serviceResult = SecondResult;
            await ResultRevocableShouldBe(SecondResult, key, "Result shouldn't have been cached");
        }

        private void SetupDateTime()
        {
            _now = DateTime.UtcNow;
            var dateTimeMock = Substitute.For<IDateTime>();
            dateTimeMock.UtcNow.Returns(_=> _isFakeTime() ? _now : DateTime.Now);
            _kernel.Rebind<IDateTime>().ToConstant(dateTimeMock);
        }

 
        private async Task SetCachingPolicyConfig(params string[][] keyValues)
        {
            bool changed = _configDic.Values.Count != 0 && keyValues.Length == 0;

            _configDic.Clear();
            foreach (var keyValue in keyValues)
            {
                var key = keyValue[0];
                var value = keyValue[1];
                if (key != null && value != null)
                {
                    _kernel.Get<OverridableConfigItems>()
                        .SetValue($"Discovery.Services.CachingTestService.CachingPolicy.{key}", value);
                    changed = true;
                }
            }
            if (changed)
            {
                await _kernel.Get<ManualConfigurationEvents>().ApplyChanges<DiscoveryConfig>();
                await Task.Delay(200);
            }
        }

        private async Task ClearCachingPolicyConfig()
        {
            await SetCachingPolicyConfig();
        }

        private async Task ResultRevocableShouldBe(string expectedResult, string key, string message = null)
        {
            var result = await _proxy.CallRevocableService(key);
            result.Value.ShouldBe(expectedResult, message);
        }

        private async Task UpdateCleanupTimeInCacheConfig(string value)
        {
            _kernel.Get<OverridableConfigItems>().SetValue($"Cache.RevokesCleanupMilliseconds", value);
            await _kernel.Get<ManualConfigurationEvents>().ApplyChanges<CacheConfig>();
            await Task.Delay(200);
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Gigya.Microdot.SharedLogic.Utils;

namespace Gigya.Microdot.SharedLogic.Collections
{
    /// <summary>
    /// A general purpose queue (FIFO) to keep items younger then a cut of time.
    /// </summary>
    /// <remarks>
    /// Items expected to be queued time sequentially while the next queued greater or equal to previous 'now'.
    /// If condition violated, the dequeue will keep items out of expected order.
    /// </remarks>
    /// <typeparam name="T"></typeparam>
    public class TimeBoundConcurrentQueue<T>
    {
        private readonly ConcurrentQueue<Tuple<T, DateTime>> _queue = new ConcurrentQueue<Tuple<T, DateTime>>();

        public int Count => _queue.Count;

        public void Enqueue(T subject)
        {
            Enqueue(subject, DateTime.UtcNow);
        }

        public void Enqueue(T subject, DateTime now)
        {
            _queue.Enqueue(new Tuple<T, DateTime>(subject, now));
        }

        /// <summary>
        /// Get an enumerable to iterate over the subjects older or equal to given time.
        /// </summary>
        /// <param name="olderThan">The cut of time to dequeue items older or equal than.</param>
        public IEnumerable<T> Dequeuing(DateTime olderThan) 
        {
            // Break, if an empty queue or an item is younger
            while (_queue.TryPeek(out var tuple) && tuple.Item2 <= olderThan)
                lock (_queue)
                    if (_queue.TryPeek(out tuple) && tuple.Item2 <= olderThan)
                        if (_queue.TryDequeue(out tuple))
                            yield return tuple.Item1;
                        else
                            GAssert.Fail("Failed to dequeue the item.");
        }
    }
}
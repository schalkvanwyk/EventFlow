﻿// The MIT License (MIT)
// 
// Copyright (c) 2015 Rasmus Mikkelsen
// Copyright (c) 2015 eBay Software Foundation
// https://github.com/rasmus/EventFlow
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
// FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
// IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// 
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventFlow.Aggregates;
using EventFlow.Core;
using EventFlow.Exceptions;
using EventFlow.Logs;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Exceptions;

namespace EventFlow.EventStores.EventStore
{
    public class EventStoreEventPersistence : IEventPersistence
    {
        private readonly ILog _log;
        private readonly IEventStoreConnection _connection;

        private class EventStoreEvent : ICommittedDomainEvent
        {
            public string AggregateId { get; set; }
            public string Data { get; set; }
            public string Metadata { get; set; }
            public int AggregateSequenceNumber { get; set; }
        }

        public EventStoreEventPersistence(
            ILog log,
            IEventStoreConnection connection)
        {
            _log = log;
            _connection = connection;
        }

        public async Task<AllCommittedEventsPage> LoadAllCommittedEvents(
            GlobalPosition globalPosition,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var nextPosition = ParsePosition(globalPosition);
            var resolvedEvents = new List<ResolvedEvent>();
            AllEventsSlice allEventsSlice;

            do
            {
                allEventsSlice = await _connection.ReadAllEventsForwardAsync(nextPosition, pageSize, false).ConfigureAwait(false);
                resolvedEvents.AddRange(allEventsSlice.Events.Where(e => !e.OriginalStreamId.StartsWith("$")));
                nextPosition = allEventsSlice.NextPosition;

            } while (resolvedEvents.Count < pageSize && !allEventsSlice.IsEndOfStream);

            var eventStoreEvents = Map(resolvedEvents);

            return new AllCommittedEventsPage(
                new GlobalPosition(string.Format("{0}-{1}", nextPosition.CommitPosition, nextPosition.PreparePosition)),
                eventStoreEvents);
        }

        private static Position ParsePosition(GlobalPosition globalPosition)
        {
            if (globalPosition.IsStart)
            {
                return Position.Start;
            }

            var parts = globalPosition.Value.Split('-');
            if (parts.Length != 2)
            {
                throw new ArgumentException(string.Format(
                    "Unknown structure for global position '{0}'. Expected it to be empty or in the form 'L-L'",
                    globalPosition.Value));
            }

            var commitPosition = long.Parse(parts[0]);
            var preparePosition = long.Parse(parts[1]);

            return new Position(commitPosition, preparePosition);
        }

        public async Task<IReadOnlyCollection<ICommittedDomainEvent>> CommitEventsAsync(
            IIdentity id,
            IReadOnlyCollection<SerializedEvent> serializedEvents,
            CancellationToken cancellationToken)
        {
            var committedDomainEvents = serializedEvents
                .Select(e => new EventStoreEvent
                    {
                        AggregateSequenceNumber = e.AggregateSequenceNumber,
                        Metadata = e.SerializedMetadata,
                        AggregateId = id.Value,
                        Data = e.SerializedData
                    })
                .ToList();

            var expectedVersion = Math.Max(serializedEvents.Min(e => e.AggregateSequenceNumber) - 1, 0);
            var eventDatas = serializedEvents
                .Select(e =>
                    {
                        var guid = Guid.Parse(e.Metadata["guid"]);
                        var eventType = string.Format("{0}.{1}.{2}", e.Metadata[MetadataKeys.AggregateName], e.Metadata.EventName, e.Metadata.EventVersion);
                        var data = Encoding.UTF8.GetBytes(e.SerializedData);
                        var meta = Encoding.UTF8.GetBytes(e.SerializedMetadata);
                        return new EventData(guid, eventType, true, data, meta);
                    })
                .ToList();

            try
            {
                using (var transaction = await _connection.StartTransactionAsync(
                    id.Value,
                    expectedVersion == 0 ? ExpectedVersion.NoStream : expectedVersion)
                    .ConfigureAwait(false))
                {
                    await transaction.WriteAsync(eventDatas).ConfigureAwait(false);
                    var writeResult = await transaction.CommitAsync().ConfigureAwait(false);
                    _log.Verbose(
                        "Wrote entity {0} with version {1} ({2},{3})",
                        id,
                        writeResult.NextExpectedVersion - 1,
                        writeResult.LogPosition.CommitPosition,
                        writeResult.LogPosition.PreparePosition);
                }
            }
            catch (WrongExpectedVersionException e)
            {
                throw new OptimisticConcurrencyException(e.Message, e);
            }

            return committedDomainEvents;
        }

        public async Task<IReadOnlyCollection<ICommittedDomainEvent>> LoadCommittedEventsAsync(
            IIdentity id,
            CancellationToken cancellationToken)
        {
            var streamEvents = new List<ResolvedEvent>();

            StreamEventsSlice currentSlice;
            var nextSliceStart = StreamPosition.Start;
            do
            {
                currentSlice = await _connection.ReadStreamEventsForwardAsync(
                    id.Value,
                    nextSliceStart,
                    200,
                    false)
                    .ConfigureAwait(false);
                nextSliceStart = currentSlice.NextEventNumber;
                streamEvents.AddRange(currentSlice.Events);

            } while (!currentSlice.IsEndOfStream);

            return Map(streamEvents);
        }

        public Task DeleteEventsAsync(IIdentity id, CancellationToken cancellationToken)
        {
            return _connection.DeleteStreamAsync(id.Value, ExpectedVersion.Any);
        }

        private static IReadOnlyCollection<EventStoreEvent> Map(IEnumerable<ResolvedEvent> resolvedEvents)
        {
            return resolvedEvents
                .Select(e => new EventStoreEvent
                    {
                        AggregateSequenceNumber = e.Event.EventNumber + 1,
                        Metadata = Encoding.UTF8.GetString(e.Event.Metadata),
                        AggregateId = e.OriginalStreamId,
                        Data = Encoding.UTF8.GetString(e.Event.Data),
                    })
                .ToList();
        }
    }
}
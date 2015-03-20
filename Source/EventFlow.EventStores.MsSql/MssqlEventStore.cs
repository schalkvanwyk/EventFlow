﻿// The MIT License (MIT)
//
// Copyright (c) 2015 EventFlow
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventFlow.Logs;
using EventFlow.MsSql;

namespace EventFlow.EventStores.MsSql
{
    public class MssqlEventStore : EventStore
    {
        public class EventDataModel : ICommittedDomainEvent
        {
            public long GlobalSequenceNumber { get; set; }
            public Guid BatchId { get; set; }
            public string AggregateId { get; set; }
            public string AggregateName { get; set; }
            public string Data { get; set; }
            public string Metadata { get; set; }
            public int AggregateSequenceNumber { get; set; }
        }

        private readonly ILog _log;
        private readonly IMssqlConnection _connection;

        public MssqlEventStore(
            ILog log,
            IEventJsonSerializer eventJsonSerializer,
            IMssqlConnection connection)
            : base(eventJsonSerializer)
        {
            _log = log;
            _connection = connection;
        }

        protected override async Task<IReadOnlyCollection<ICommittedDomainEvent>> CommitEventsAsync<TAggregate>(
            string id,
            int oldVersion,
            int newVersion,
            IReadOnlyCollection<SerializedEvent> serializedEvents)
        {
            var batchId = Guid.NewGuid();
            var aggregateType = typeof(TAggregate);
            var aggregateName = aggregateType.Name.Replace("Aggregate", string.Empty);
            var eventDataModels = serializedEvents
                .Select((e, i) => new EventDataModel
                    {
                        AggregateId = id,
                        AggregateName = aggregateName,
                        BatchId = batchId,
                        Data = e.Data,
                        Metadata = e.Meta,
                        AggregateSequenceNumber = oldVersion + 1 + i
                    })
                .ToList();

            const string sql = @"
                INSERT INTO
                    EventSource
                        (BatchId, AggregateId, AggregateName, Data, Metadata, AggregateSequenceNumber)
                    VALUES
                        (@BatchId, @AggregateId, @AggregateName, @Data, @Metadata, @AggregateSequenceNumber);
                SELECT CAST(SCOPE_IDENTITY() as bigint);";

            foreach (var eventDataModel in eventDataModels)
            {
                eventDataModel.GlobalSequenceNumber = (await _connection.QueryAsync<long>(sql, eventDataModel).ConfigureAwait(false)).Single();
            }

            return eventDataModels;
        }

        protected override async Task<IReadOnlyCollection<ICommittedDomainEvent>> LoadCommittedEventsAsync(string id)
        {
            const string sql = @"SELECT * FROM EventSource WHERE AggregateId = @AggregateId ORDER BY AggregateSequenceNumber ASC";
            var eventDataModels = await _connection.QueryAsync<EventDataModel>(sql, new { AggregateId = id }).ConfigureAwait(false);
            return eventDataModels;
        }
    }
}

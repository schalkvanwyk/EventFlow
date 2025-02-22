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
using System.Threading;
using System.Threading.Tasks;
using EventFlow.EventStores;
using EventFlow.Extensions;
using EventFlow.Jobs;
using EventFlow.Provided.Jobs;
using EventFlow.TestHelpers;
using EventFlow.TestHelpers.Aggregates;
using EventFlow.TestHelpers.Aggregates.Commands;
using EventFlow.TestHelpers.Aggregates.ValueObjects;
using FluentAssertions;
using NUnit.Framework;

namespace EventFlow.Tests.UnitTests.Jobs
{
    public class JobsFlowTests
    {
        [Test]
        public async Task Flow()
        {
            using (var resolver = EventFlowOptions.New
                .AddDefaults(EventFlowTestHelpers.Assembly)
                .CreateResolver(false))
            {
                // Arrange
                var testId = ThingyId.New;
                var pingId = PingId.New;
                var jobScheduler = resolver.Resolve<IJobScheduler>();
                var eventStore = resolver.Resolve<IEventStore>();
                var executeCommandJob = PublishCommandJob.Create(new ThingyPingCommand(testId, pingId), resolver);

                // Act
                await jobScheduler.ScheduleNowAsync(executeCommandJob, CancellationToken.None).ConfigureAwait(false);

                // Assert
                var testAggregate = await eventStore.LoadAggregateAsync<ThingyAggregate, ThingyId>(testId, CancellationToken.None).ConfigureAwait(false);
                testAggregate.IsNew.Should().BeFalse();
            }
        }
    }
}

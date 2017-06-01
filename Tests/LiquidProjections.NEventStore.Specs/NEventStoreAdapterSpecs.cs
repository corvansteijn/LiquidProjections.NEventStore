﻿using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chill;
using FakeItEasy;
using FluentAssertions;
using LiquidProjections.Abstractions;
using NEventStore;
using NEventStore.Persistence;
using Xunit;

namespace LiquidProjections.NEventStore.Specs
{
    namespace EventStoreClientSpecs
    {
        public class When_the_persistency_engine_is_temporarily_unavailable : GivenSubject<CreateSubscription>
        {
            private readonly TimeSpan pollingInterval = 1.Seconds();
            private Transaction actualTransaction;

            public When_the_persistency_engine_is_temporarily_unavailable()
            {
                Given(() =>
                {
                    UseThe((ICommit) new CommitBuilder().WithCheckpoint("123").Build());

                    var eventStore = A.Fake<IPersistStreams>();
                    A.CallTo(() => eventStore.GetFrom(A<string>.Ignored)).Returns(new[] {The<ICommit>()});
                    A.CallTo(() => eventStore.GetFrom(A<string>.Ignored)).Throws(new ApplicationException()).Once();

                    var adapter = new NEventStoreAdapter(eventStore, 11, pollingInterval, 100, () => DateTime.UtcNow);

                    WithSubject(_ => adapter.Subscribe);
                });

                When(() =>
                {
                    Subject(null, new Subscriber
                    {
                        HandleTransactions = (transactions, info) =>
                        {
                            actualTransaction = transactions.First();
                            return Task.FromResult(0);
                        }
                    }, "someId");
                });
            }

            [Fact]
            public async Task Then_it_should_recover_automatically_after_its_polling_interval_expires()
            {
                do
                {
                    await Task.Delay(pollingInterval);
                }
                while (actualTransaction == null);

                actualTransaction.Id.Should().Be(The<ICommit>().CommitId.ToString());
            }
        }

        public class When_a_commit_is_persisted : GivenSubject<CreateSubscription>
        {
            private readonly TimeSpan pollingInterval = 1.Seconds();
            private readonly TaskCompletionSource<Transaction> transactionHandledSource = new TaskCompletionSource<Transaction>();

            public When_a_commit_is_persisted()
            {
                Given(() =>
                {
                    UseThe((ICommit) new CommitBuilder().WithCheckpoint("123").Build());

                    var eventStore = A.Fake<IPersistStreams>();
                    A.CallTo(() => eventStore.GetFrom(A<string>.Ignored)).Returns(new[] {The<ICommit>()});

                    var adapter = new NEventStoreAdapter(eventStore, 11, pollingInterval, 100, () => DateTime.UtcNow);

                    WithSubject(_ => adapter.Subscribe);
                });

                When(() =>
                {
                    Subject(null, new Subscriber
                    {
                        HandleTransactions = (transactions, info) =>
                        {
                            transactionHandledSource.SetResult(transactions.First());

                            return Task.FromResult(0);
                        }
                    }, "someId");
                });
            }

            [Fact]
            public async Task Then_it_should_convert_the_commit_details_to_a_transaction()
            {
                Transaction actualTransaction = await transactionHandledSource.Task.TimeoutAfter(30.Seconds());
                ;

                var commit = The<ICommit>();
                actualTransaction.Id.Should().Be(commit.CommitId.ToString());
                actualTransaction.Checkpoint.Should().Be(long.Parse(commit.CheckpointToken));
                actualTransaction.TimeStampUtc.Should().Be(commit.CommitStamp);
                actualTransaction.StreamId.Should().Be(commit.StreamId);

                actualTransaction.Events.ShouldBeEquivalentTo(commit.Events, options => options.ExcludingMissingMembers());
            }
        }

        public class When_requesting_a_subscription_beyond_the_highest_available_checkpoint : GivenSubject<CreateSubscription>
        {
            private readonly TimeSpan pollingInterval = 1.Seconds();
            private readonly TaskCompletionSource<Transaction> transactionHandledSource = new TaskCompletionSource<Transaction>();

            public When_requesting_a_subscription_beyond_the_highest_available_checkpoint()
            {
                Given(() =>
                {
                    UseThe((ICommit) new CommitBuilder().WithCheckpoint("2").Build());

                    var eventStore = A.Fake<IPersistStreams>();
                    A.CallTo(() => eventStore.GetFrom(A<string>.Ignored)).Returns(new[] {The<ICommit>()});

                    var adapter = new NEventStoreAdapter(eventStore, 11, pollingInterval, 100, () => DateTime.UtcNow);

                    WithSubject(_ => adapter.Subscribe);
                });

                When(() =>
                {
                    Subject(1000, new Subscriber
                    {
                        HandleTransactions = (transactions, info) =>
                        {
                            transactionHandledSource.SetResult(transactions.First());

                            return Task.FromResult(0);
                        }
                    }, "myIde");
                });
            }

            [Fact]
            public async Task Then_it_should_provide_the_highest_available_commit()
            {
                Transaction actualTransaction = await transactionHandledSource.Task.TimeoutAfter(30.Seconds());
                ;

                actualTransaction.Id.Should().Be(The<ICommit>().CommitId.ToString());
                actualTransaction.Checkpoint.Should().Be(long.Parse(The<ICommit>().CheckpointToken));
                actualTransaction.TimeStampUtc.Should().Be(The<ICommit>().CommitStamp);
                actualTransaction.StreamId.Should().Be(The<ICommit>().StreamId);

                actualTransaction.Events.ShouldBeEquivalentTo(The<ICommit>().Events,
                    options => options.ExcludingMissingMembers());
            }
        }

        public class When_there_are_no_more_commits : GivenSubject<CreateSubscription>
        {
            private readonly TimeSpan pollingInterval = 500.Milliseconds();
            private DateTime utcNow = DateTime.UtcNow;
            private IPersistStreams eventStore;
            private readonly TaskCompletionSource<object> eventStoreQueriedSource = new TaskCompletionSource<object>();

            public When_there_are_no_more_commits()
            {
                Given(() =>
                {
                    eventStore = A.Fake<IPersistStreams>();
                    A.CallTo(() => eventStore.GetFrom(A<string>.Ignored))
                        .Invokes(_ =>
                        {
                            eventStoreQueriedSource.SetResult(null);
                        })
                        .Returns(new ICommit[0]);

                    var adapter = new NEventStoreAdapter(eventStore, 11, pollingInterval, 100, () => DateTime.UtcNow);
                    WithSubject(_ => adapter.Subscribe);

                    Subject(1000, new Subscriber
                    {
                        HandleTransactions = (transactions, info) => Task.FromResult(0)
                    }, "someId");
                });

                When(async () =>
                {
                    await eventStoreQueriedSource.Task.TimeoutAfter(30.Seconds());
                    ;
                });
            }

            [Fact]
            public void Then_it_should_wait_for_the_polling_interval_to_retry()
            {
                A.CallTo(() => eventStore.GetFrom(A<string>.Ignored)).MustHaveHappened(Repeated.Exactly.Once);

                utcNow = utcNow.Add(1.Seconds());

                A.CallTo(() => eventStore.GetFrom(A<string>.Ignored)).MustHaveHappened(Repeated.Exactly.Once);
            }
        }

        public class When_a_commit_is_already_projected : GivenSubject<CreateSubscription>
        {
            private readonly TimeSpan pollingInterval = 1.Seconds();
            private readonly TaskCompletionSource<Transaction> transactionHandledSource = new TaskCompletionSource<Transaction>();

            public When_a_commit_is_already_projected()
            {
                Given(() =>
                {
                    ICommit projectedCommit = new CommitBuilder().WithCheckpoint("123").Build();
                    ICommit unprojectedCommit = new CommitBuilder().WithCheckpoint("124").Build();

                    var eventStore = A.Fake<IPersistStreams>();
                    A.CallTo(() => eventStore.GetFrom(A<string>.Ignored)).Returns(new[] {projectedCommit, unprojectedCommit});
                    A.CallTo(() => eventStore.GetFrom("123")).Returns(new[] {unprojectedCommit});

                    var adapter = new NEventStoreAdapter(eventStore, 11, pollingInterval, 100, () => DateTime.UtcNow);
                    WithSubject(_ => adapter.Subscribe);
                });

                When(() =>
                {
                    Subject(123, new Subscriber
                    {
                        HandleTransactions = (transactions, info) =>
                        {
                            transactionHandledSource.SetResult(transactions.First());

                            return Task.FromResult(0);
                        }
                    }, "someId");
                });
            }

            [Fact]
            public async Task Then_it_should_convert_the_unprojected_commit_details_to_a_transaction()
            {
                Transaction actualTransaction = await transactionHandledSource.Task.TimeoutAfter(30.Seconds());
                ;

                actualTransaction.Checkpoint.Should().Be(124);
            }
        }

        public class When_disposing : GivenSubject<NEventStoreAdapter>
        {
            private readonly TimeSpan pollingInterval = 500.Milliseconds();
            private readonly DateTime utcNow = DateTime.UtcNow;
            private IPersistStreams eventStore;

            public When_disposing()
            {
                Given(() =>
                {
                    eventStore = A.Fake<IPersistStreams>();
                    A.CallTo(() => eventStore.GetFrom(A<string>.Ignored)).Returns(new ICommit[0]);

                    WithSubject(_ => new NEventStoreAdapter(eventStore, 11, pollingInterval, 100, () => utcNow));

                    Subject.Subscribe(null, new Subscriber
                    {
                        HandleTransactions = (transactions, info) => Task.FromResult(0)
                    }, "someId");
                });

                When(() => Subject.Dispose(), deferredExecution: true);
            }

            [Fact]
            public void Then_it_should_stop()
            {
                if (!Task.Run(() => WhenAction.ShouldNotThrow()).Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new InvalidOperationException("The adapter has not stopped in 10 seconds.");
                }
            }
        }

        public class When_disposing_subscription : GivenSubject<NEventStoreAdapter>
        {
            private readonly TimeSpan pollingInterval = 500.Milliseconds();
            private readonly DateTime utcNow = DateTime.UtcNow;
            private IPersistStreams eventStore;
            private IDisposable subscription;

            public When_disposing_subscription()
            {
                Given(() =>
                {
                    eventStore = A.Fake<IPersistStreams>();
                    A.CallTo(() => eventStore.GetFrom(A<string>.Ignored)).Returns(new ICommit[0]);

                    WithSubject(_ => new NEventStoreAdapter(eventStore, 11, pollingInterval, 100, () => utcNow));

                    subscription = Subject.Subscribe(null, new Subscriber
                    {
                        HandleTransactions = (transactions, info) => Task.FromResult(0)
                    }, "someId");
                });

                When(() => subscription.Dispose(), deferredExecution: true);
            }

            [Fact]
            public void Then_it_should_stop()
            {
                if (!Task.Run(() => WhenAction.ShouldNotThrow()).Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new InvalidOperationException("The subscription has not stopped in 10 seconds.");
                }
            }
        }

        public class
            When_a_subscription_starts_after_zero_checkpoint_and_another_subscription_starts_after_null_checkpoint_while_the_first_subscription_is_loading_the_first_page_from_the_event_store :
                GivenSubject<CreateSubscription>
        {
            private readonly TimeSpan pollingInterval = 500.Milliseconds();
            private readonly DateTime utcNow = DateTime.UtcNow;
            private IPersistStreams eventStore;
            private readonly ManualResetEventSlim aSubscriptionStartedLoading = new ManualResetEventSlim();
            private readonly ManualResetEventSlim secondSubscriptionCreated = new ManualResetEventSlim();
            private readonly ManualResetEventSlim secondSubscriptionReceivedTheTransaction = new ManualResetEventSlim();

            public
                When_a_subscription_starts_after_zero_checkpoint_and_another_subscription_starts_after_null_checkpoint_while_the_first_subscription_is_loading_the_first_page_from_the_event_store()
            {
                Given(() =>
                {
                    eventStore = A.Fake<IPersistStreams>();
                    A.CallTo(() => eventStore.GetFrom(A<string>.Ignored)).ReturnsLazily(call =>
                    {
                        string checkpointString = call.GetArgument<string>(0);

                        long checkpoint = string.IsNullOrEmpty(checkpointString)
                            ? 0
                            : long.Parse(checkpointString, CultureInfo.InvariantCulture);

                        aSubscriptionStartedLoading.Set();

                        if (!secondSubscriptionCreated.Wait(TimeSpan.FromSeconds(10)))
                        {
                            throw new InvalidOperationException("The second subscription has not been created in 10 seconds.");
                        }

                        // Give the second subscription enough time to access the cache.
                        Thread.Sleep(TimeSpan.FromSeconds(1));

                        return checkpoint > 0
                            ? new ICommit[0]
                            : new ICommit[] {new CommitBuilder().WithCheckpoint("1").Build()};
                    });

                    var adapter = new NEventStoreAdapter(eventStore, 11, pollingInterval, 100, () => DateTime.UtcNow);
                    WithSubject(_ => adapter.Subscribe);
                });

                When(() =>
                {
                    Subject(
                        0,
                        new Subscriber
                        {
                            HandleTransactions = (transactions, info) => Task.FromResult(0)
                        },
                        "firstId");

                    if (!aSubscriptionStartedLoading.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new InvalidOperationException("The first subscription has not started loading in 10 seconds.");
                    }

                    Subject(
                        null,
                        new Subscriber
                        {
                            HandleTransactions = (transactions, info) =>
                            {
                                secondSubscriptionReceivedTheTransaction.Set();
                                return Task.FromResult(0);
                            }
                        },
                        "secondId"
                    );

                    secondSubscriptionCreated.Set();
                });
            }

            [Fact]
            public void Then_the_second_subscription_should_not_hang()
            {
                if (!secondSubscriptionReceivedTheTransaction.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new InvalidOperationException("The second subscription has not got the transaction in 10 seconds.");
                }
            }
        }
    }
}
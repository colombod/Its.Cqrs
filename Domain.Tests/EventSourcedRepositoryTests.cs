// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Its.Domain.Serialization;
using Microsoft.Its.Domain.Testing;
using Microsoft.Its.Recipes;
using NUnit.Framework;
using Sample.Domain;
using Sample.Domain.Ordering;
using Sample.Domain.Ordering.Commands;

namespace Microsoft.Its.Domain.Tests
{
    [TestFixture]
    public abstract class EventSourcedRepositoryTests
    {
        private CompositeDisposable disposables;

        protected abstract void Configure(Configuration configuration, Action onSave = null);

        protected abstract IEventSourcedRepository<TAggregate> CreateRepository<TAggregate>(
            Action onSave = null)
            where TAggregate : class, IEventSourced;

        [SetUp]
        public virtual void SetUp()
        {
            // disable authorization checks
            Command<Order>.AuthorizeDefault = (order, command) => true;
            Command<CustomerAccount>.AuthorizeDefault = (order, command) => true;

            var configuration = new Configuration();

            Configure(configuration);

            disposables = new CompositeDisposable
            {
                ConfigurationContext.Establish(configuration),
                configuration
            };
        }

        [TearDown]
        public virtual void TearDown()
        {
            Clock.Reset();
            disposables.Dispose();
        }

        [Test]
        public void Serialized_events_are_deserialized_in_their_correct_sequence_and_type()
        {
            var order = new Order();
            var repository = CreateRepository<Order>();
            order
                .Apply(new AddItem
                {
                    ProductName = "Widget",
                    Price = 10m,
                    Quantity = 1
                })
                .Apply(new ChangeFufillmentMethod { FulfillmentMethod = FulfillmentMethod.DirectShip });

            repository.Save(order);
            var rehydratedOrder = repository.GetLatest(order.Id);

            rehydratedOrder.EventHistory.Count().Should().Be(3);
            rehydratedOrder.EventHistory.Skip(1).First().Should().BeOfType<Order.ItemAdded>();
            rehydratedOrder.EventHistory.Last().Should().BeOfType<Order.FulfillmentMethodSelected>();
        }

        [Test]
        public void When_an_aggregate_id_is_not_found_then_GetLatest_returns_null()
        {
            var aggregate = CreateRepository<Order>().GetLatest(Any.Guid());

            aggregate.Should().BeNull();
        }

        [Test]
        public void GetVersion_does_not_pull_versions_after_the_specified_one()
        {
            var order = new Order();
            var repository = CreateRepository<Order>();

            Enumerable.Range(1, 10).ForEach(i => order.Apply(new AddItem
            {
                ProductName = "Widget",
                Price = 10m,
                Quantity = i
            }));

            repository.Save(order);
            var rehydratedOrder = repository.GetVersion(order.Id, 4);

            rehydratedOrder.Version().Should().Be(4);
            rehydratedOrder.EventHistory.Count().Should().Be(4);
            rehydratedOrder.Items.First().Quantity.Should().Be(6);
        }

        [Test]
        public void GetAsOfDate_does_not_pull_events_after_the_specified_date()
        {
            var order = new Order();
            var repository = CreateRepository<Order>();
            var startTime = DateTimeOffset.UtcNow;

            Enumerable.Range(1, 10).ForEach(i =>
            {
                Clock.Now = () => startTime.AddDays(i);
                order.Apply(new AddItem
                {
                    ProductName = "Widget",
                    Price = 10m,
                    Quantity = i
                });
            });

            repository.Save(order);
            var rehydratedOrder = repository.GetAsOfDate(order.Id, startTime.AddDays(3.1));

            rehydratedOrder.Version().Should().Be(4);
            rehydratedOrder.EventHistory.Count().Should().Be(4);
            rehydratedOrder.Items.First().Quantity.Should().Be(6);
        }

        [Test]
        public void Deserialized_events_are_used_to_rebuild_the_state_of_the_aggregate()
        {
            var order = new Order();
            var repository = CreateRepository<Order>();
            order
                .Apply(new AddItem
                {
                    ProductName = "Widget",
                    Price = 10m,
                    Quantity = 2
                })
                .Apply(new AddItem
                {
                    ProductName = "Widget",
                    Price = 10m,
                    Quantity = 3
                });

            repository.Save(order);
            var rehydratedOrder = repository.GetLatest(order.Id);

            rehydratedOrder.Items.Single().Quantity.Should().Be(5);
        }

        [Test]
        public void When_Save_is_called_then_each_added_event_is_published()
        {
            var order = new Order();
            var repository = CreateRepository<Order>();

            order
                .Apply(new AddItem
                {
                    ProductName = "Widget",
                    Price = 10m,
                    Quantity = 2
                })
                .Apply(new ChangeFufillmentMethod
                {
                    FulfillmentMethod = FulfillmentMethod.Delivery
                });
            repository.Save(order);

            var bus = Configuration.Current.EventBus as FakeEventBus;
            bus.PublishedEvents().Count()
               .Should().Be(3);
            bus.PublishedEvents().Skip(1).First()
               .Should().BeOfType<Order.ItemAdded>();
            bus.PublishedEvents().Skip(1).Skip(1).First()
               .Should().BeOfType<Order.FulfillmentMethodSelected>();
        }

        [Test]
        public void When_Save_is_called_then_published_events_have_incrementing_sequence_ids()
        {
            // set up the repository so we're not starting from the beginning
            var order = new Order();
            var bus = Configuration.Current.EventBus as FakeEventBus;
            var repository = CreateRepository<Order>();
            order.Apply(new AddItem
            {
                ProductName = "Widget",
                Price = 10m,
                Quantity = 2
            });
            repository.Save(order);
            bus.Clear();

            // apply 2 commands
            var order2 = repository.GetLatest(order.Id);
            order2
                .Apply(new ChangeFufillmentMethod { FulfillmentMethod = FulfillmentMethod.Delivery })
                .Apply(new ChangeCustomerInfo { CustomerName = "Wanda" });
            repository.Save(order2);

            bus.PublishedEvents().Count().Should().Be(2);
            bus.PublishedEvents().First().SequenceNumber.Should().Be(3);
            bus.PublishedEvents().Skip(1).First().SequenceNumber.Should().Be(4);
        }

        [Test]
        public abstract void When_storage_fails_then_no_events_are_published();

        [Test]
        public void Concurrency_on_event_storage_is_optimistic_and_exceptions_have_informative_messages()
        {
            var repository = CreateRepository<Order>();
            var order = new Order()
                .Apply(new AddItem
                {
                    Price = .05m,
                    ProductName = "widget",
                    Quantity = 100
                })
                .Apply(new ChangeCustomerInfo
                {
                    CustomerName = "Wanda"
                });
            repository.Save(order);

            var order2 = repository.GetLatest(order.Id)
                                   .Apply(new ChangeCustomerInfo
                                   {
                                       CustomerName = "Alice"
                                   });
            var order3 = repository.GetLatest(order.Id)
                                   .Apply(new ChangeCustomerInfo
                                   {
                                       CustomerName = "Bob"
                                   });

            repository.Save(order2);

            repository.Invoking(r => r.Save(order3))
                      .ShouldThrow<ConcurrencyException>()
                      .And
                      .Message
                      .Should()
                      .Contain("Alice")
                      .And
                      .Contain("Bob")
                      .And
                      .Contain("Sample.Domain.Ordering.Order+CustomerInfoChanged");
        }

        [Test]
        public abstract void Events_that_cannot_be_deserialized_due_to_unknown_type_do_not_cause_sourcing_to_fail();

        [Test]
        public abstract void Events_at_the_end_of_the_sequence_that_cannot_be_deserialized_due_to_unknown_type_do_not_cause_Version_to_be_incorrect();

        [Test]
        public abstract void Events_that_cannot_be_deserialized_due_to_unknown_member_do_not_cause_sourcing_to_fail();

        protected abstract void SaveEventsDirectly(params object[] events);

        [Test]
        public void Save_transfers_pending_events_to_event_history()
        {
            var order = new Order();
            var repository = CreateRepository<Order>();
            order.Apply(new AddItem { Price = 1m, ProductName = "Widget" });
            repository.Save(order);

            order.EventHistory.Count().Should().Be(2);
        }

        [Test]
        public void After_Save_additional_events_continue_in_the_correct_sequence()
        {
            var order = new Order();
            var bus = Configuration.Current.EventBus as FakeEventBus;
            var repository = CreateRepository<Order>();
            var addEvent = new Action(() =>
                                      order.Apply(new AddItem { Price = 1m, ProductName = "Widget" }));

            addEvent();
            repository.Save(order);
            bus.PublishedEvents().Last().SequenceNumber.Should().Be(2);

            addEvent();
            addEvent();
            repository.Save(order);
            bus.PublishedEvents().Last().SequenceNumber.Should().Be(4);

            addEvent();
            addEvent();
            addEvent();
            repository.Save(order);
            bus.PublishedEvents().Last().SequenceNumber.Should().Be(7);
        }

        [Test]
        public void Refresh_can_be_used_to_update_an_aggregate_in_memory_with_the_latest_events_from_the_stream()
        {
            // arrange
            var order = new Order()
                .Apply(new ChangeCustomerInfo
                {
                    CustomerName = Any.FullName()
                });

            var repository = CreateRepository<Order>();
            repository.Save(order);

            repository.Save(
                repository.GetLatest(order.Id)
                          .Apply(new AddItem
                          {
                              Price = 1,
                              ProductName = Any.Word()
                          }).Apply(new AddItem
                          {
                              Price = 2,
                              ProductName = Any.Word()
                          }));

            // act
            repository.Refresh(order);

            // assert
            order.Version().Should().Be(4);
        }
        
        [Test]
        public void After_Refresh_is_called_then_future_events_are_added_at_the_correct_sequence_number()
        {
            // arrange
            var order = new Order()
                .Apply(new ChangeCustomerInfo
                {
                    CustomerName = Any.FullName(),
                });

            var repository = CreateRepository<Order>();
            repository.Save(order);

            repository.Save(
                repository.GetLatest(order.Id)
                          .Apply(new AddItem
                          {
                              Price = 1,
                              ProductName = Any.Word()
                          }).Apply(new AddItem
                          {
                              Price = 2,
                              ProductName = Any.Word()
                          }));
            repository.Refresh(order);

            // act
            order.Apply(new Cancel());

            Console.WriteLine(order.Events().ToJson());

            // assert
            order.Version().Should().Be(5);
            order.PendingEvents.Last().SequenceNumber.Should().Be(5);
        }

        [Test]
        public void When_Refresh_is_called_on_an_aggregate_having_pending_events_it_throws()
        {
            var order = new Order()
                .Apply(new ChangeCustomerInfo
                {
                    CustomerName = Any.FullName()
                });

            var repository = CreateRepository<Order>();

            // act
            Action refresh = () => repository.Refresh(order);

            // assert
            refresh.ShouldThrow<InvalidOperationException>()
                   .And
                   .Message
                   .Should()
                   .Contain("Aggregates having pending events cannot be updated.");
        }

        [Test]
        public void GetAggregate_can_be_used_within_a_consequenter_to_access_an_aggregate_without_having_to_re_source()
        {
            // arrange
            var order = new Order()
                .Apply(new ChangeCustomerInfo
                {
                    CustomerName = Any.FullName()
                })
                .Apply(new AddItem
                {
                    ProductName = "Cog",
                    Price = 9.99m
                })
                .Apply(new AddItem
                {
                    ProductName = "Sprocket",
                    Price = 9.99m
                })
                .Apply(new ProvideCreditCardInfo
                {
                    CreditCardNumber = Any.String(16, 16, Characters.Digits),
                    CreditCardCvv2 = "123",
                    CreditCardExpirationMonth = "12",
                    CreditCardName = Any.FullName(),
                    CreditCardExpirationYear = "16"
                })
                .Apply(new SpecifyShippingInfo())
                .Apply(new Place());

            var repository = CreateRepository<Order>();
            Configuration.Current.UseDependency<IEventSourcedRepository<Order>>(t =>
                                                                                {
                                                                                    throw new Exception("GetAggregate should not be triggering this call.");
                                                                                });
            Order aggregate = null;
            var consquenter = Consequenter.Create<Order.Placed>(e => { aggregate = e.GetAggregate(); });
            var bus = Configuration.Current.EventBus as FakeEventBus;
            bus.Subscribe(consquenter);

            // act
            repository.Save(order);

            // assert
            aggregate.Should().Be(order);
        }

        [Test]
        public void GetAggregate_can_be_used_when_no_aggregate_was_previously_sourced()
        {
            var order = new Order()
                .Apply(new ChangeCustomerInfo
                {
                    CustomerName = Any.FullName()
                });

            var repository = CreateRepository<Order>();
            repository.Save(order);
            Order aggregate = null;
            var consquenter = Consequenter.Create<Order.Placed>(e =>
            {
                aggregate = e.GetAggregate();
            });
            consquenter.HaveConsequences(new Order.Placed
            {
                AggregateId = order.Id
            });

            aggregate.Id.Should().Be(order.Id);
        }

        [Test]
        public async Task GetLatest_can_return_an_aggregate_built_from_a_snapshot_projection()
        {
            // arrange
            var snapshotRepository = new InMemorySnapshotRepository();
            Configuration.Current.UseDependency<ISnapshotRepository>(_ => snapshotRepository);

            var snapshot = new CustomerAccountSnapshot
                           {
                               AggregateId = Any.Guid(),
                               Version = 123,
                               AggregateTypeName = AggregateType<CustomerAccount>.EventStreamName,
                               EmailAddress = Any.Email(),
                               NoSpam = true,
                               UserName = Any.FullName(),
                               ETags = new[] { Any.Word(), Any.Word() }
                           };
            await snapshotRepository.SaveSnapshot(snapshot);

            // act
            var account = CreateRepository<CustomerAccount>().GetLatest(snapshot.AggregateId);

            // assert
            account.Id.Should().Be(snapshot.AggregateId);
            account.Version.Should().Be(snapshot.Version);
            account.EmailAddress.Should().Be(snapshot.EmailAddress);
            account.UserName.Should().Be(snapshot.UserName);
            account.NoSpam.Should().Be(snapshot.NoSpam);
            
            foreach (var etag in snapshot.ETags)
            {
                account.HasETag(etag).Should().BeTrue("etags are expected to be loaded from the snapshot");
            }
        }

        [Test]
        public async Task When_new_events_are_added_to_an_aggregate_sourced_from_a_fully_current_snapshot_the_version_increments_correctly()
        {
             // arrange
            var snapshotRepository = new InMemorySnapshotRepository();
            Configuration.Current.UseDependency<ISnapshotRepository>(_ => snapshotRepository);

            var snapshot = new CustomerAccountSnapshot
                           {
                               AggregateId = Any.Guid(),
                               Version = 123,
                               AggregateTypeName = AggregateType<CustomerAccount>.EventStreamName,
                               EmailAddress = Any.Email(),
                               NoSpam = true,
                               UserName = Any.FullName(),
                               ETags = new[] { Any.Word(), Any.Word() }
                           };
            await snapshotRepository.SaveSnapshot(snapshot);

            // act
            var account = CreateRepository<CustomerAccount>().GetLatest(snapshot.AggregateId);
            account.Apply(new RequestSpam());

            // assert
            account.Version.Should().Be(124);
        }

        [Test]
        public async Task When_new_events_are_added_to_an_aggregate_sourced_from_a_stale_snapshot_the_version_increments_correctly()
        {
             // arrange
            var snapshotRepository = new InMemorySnapshotRepository();
            Configuration.Current.UseDependency<ISnapshotRepository>(_ => snapshotRepository);

            var snapshot = new CustomerAccountSnapshot
                           {
                               AggregateId = Any.Guid(),
                               Version = 122,
                               AggregateTypeName = AggregateType<CustomerAccount>.EventStreamName,
                               EmailAddress = Any.Email(),
                               NoSpam = true,
                               UserName = Any.FullName(),
                               ETags = new[] { Any.Word(), Any.Word() }
                           };

            await snapshotRepository.SaveSnapshot(snapshot);
            SaveEventsDirectly(new CustomerAccount.RequestedSpam
            {
                AggregateId = snapshot.AggregateId,
                SequenceNumber = snapshot.Version + 1
            });
            

            // act
            var account = CreateRepository<CustomerAccount>().GetLatest(snapshot.AggregateId);
            account.Apply(new RequestSpam());

            // assert
            account.Version.Should().Be(124);
        }
        
        [Test]
        public async Task When_a_snapshot_is_not_up_to_date_then_GetLatest_retrieves_later_events_and_applies_them()
        {
            // arrange
            var snapshotRepository = new InMemorySnapshotRepository();
            Configuration.Current.UseDependency<ISnapshotRepository>(_ => snapshotRepository);

            var snapshot = new CustomerAccountSnapshot
                           {
                               AggregateId = Any.Guid(),
                               Version = 123,
                               AggregateTypeName = AggregateType<CustomerAccount>.EventStreamName,
                               EmailAddress = Any.Email(),
                               NoSpam = true,
                               UserName = Any.FullName(),
                               ETags = new[] { Any.Word(), Any.Word() }
                           };
            await snapshotRepository.SaveSnapshot(snapshot);
            SaveEventsDirectly(new CustomerAccount.RequestedSpam
                               {
                                   AggregateId = snapshot.AggregateId,
                                   SequenceNumber = 124
                               });

            // act
            var account = CreateRepository<CustomerAccount>().GetLatest(snapshot.AggregateId);

            // assert
            account.Version.Should().Be(124);
            account.NoSpam.Should().Be(false);
        }

        [Ignore("Scenario under consideration")]
        [Test]
        public async Task Snapshotting_can_be_bypassed()
        {
            var snapshotRepository = new InMemorySnapshotRepository();
            Configuration.Current.UseDependency<ISnapshotRepository>(_ => snapshotRepository);

            var account = new CustomerAccount()
                .Apply(new ChangeEmailAddress(Any.Email()))
                .Apply(new RequestSpam())
                .Apply(new SendMarketingEmail())
                .Apply(new SendOrderConfirmationEmail(Any.AlphanumericString(8, 8)));

            var eventSourcedRepository = CreateRepository<CustomerAccount>();
            eventSourcedRepository.Save(account);
            await snapshotRepository.SaveSnapshot(account);

            // act
            account = eventSourcedRepository.GetLatest(account.Id);

            // assert
            account.Events().Count()
                   .Should()
                   .Be(4);
        }
    }
}

﻿using System;
using Banking.Domain.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Its.Domain;
using Microsoft.Its.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NUnit.Framework;

namespace Banking.Domain.Tests
{
    [TestClass, TestFixture]
    public class WithdrawalTests
    {
        private CheckingAccount account;

        [SetUp, TestInitialize]
        public void SetUp()
        {
            account = new CheckingAccount(Guid.NewGuid(), new IEvent[]
            {
                new CheckingAccount.Opened
                {
                    CustomerAccountId = Guid.NewGuid()
                },
                new CheckingAccount.FundsDeposited
                {
                    Amount = Any.PositiveInt(100)
                }
            });

            Authorization.AuthorizeAllCommands();
        }

        [Test]
        public void When_a_withdrawal_is_made_then_the_balance_reflects_it()
        {
            var startingBalance = account.Balance;
            var withdrawalAmount = Any.Decimal(1, 2000);
            account.Apply(new WithdrawFunds
            {
                Amount = withdrawalAmount
            });

            account.Balance.Should().Be(startingBalance - withdrawalAmount);
        }

        [Test]
        public void A_withdrawal_cannot_be_made_for_a_negative_amount()
        {
            Action withdraw = () => account.Apply(new WithdrawFunds
            {
                Amount = Any.Decimal(-2000, -1)
            });

            withdraw.ShouldThrow<CommandValidationException>()
                    .And
                    .Message.Should().Contain("You cannot make a withdrawal for a negative amount.");
        }

        [Test]
        public void A_withdrawal_cannot_be_made_from_a_closed_account()
        {
            account
                .Apply(new WithdrawFunds
                {
                    Amount = account.Balance
                })
                .Apply(new CloseCheckingAccount());

            Action withdraw = () => account.Apply(new WithdrawFunds
            {
                Amount = Any.Decimal(1, 100)
            });

            withdraw.ShouldThrow<CommandValidationException>()
                    .And
                    .Message.Should().Contain("You cannot make a withdrawal from a closed account.");
        }
    }
}
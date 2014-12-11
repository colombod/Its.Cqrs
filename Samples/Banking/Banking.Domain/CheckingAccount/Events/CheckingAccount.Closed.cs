﻿using Microsoft.Its.Domain;

namespace Sample.Banking.Domain
{
    public partial class CheckingAccount
    {
        public class Closed : Event<CheckingAccount>
        {
            public override void Update(CheckingAccount aggregate)
            {
                aggregate.DateClosed = Timestamp;
            }
        }
    }
}
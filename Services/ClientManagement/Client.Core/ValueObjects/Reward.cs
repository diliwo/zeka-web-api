﻿using ClientManagement.Core.Common;

namespace ClientManagement.Core.ValueObjects
{
    public class Reward : ValueObject
    {
        public string Type { get; set; }
        public Reward(){ }
        public Reward(string type) => Type = type;
        protected override IEnumerable<object> GetEqualityComponents()
        {
            throw new System.NotImplementedException();
        }

    }
}
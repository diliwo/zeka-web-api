﻿using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities;

public class NatureOfContract : Entity
{
    public string Name { get; set; }

    public NatureOfContract(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        Name = name;
    }
}
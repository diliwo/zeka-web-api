﻿using System;

namespace Cpas.Zeka.Domain.Beneficiaries.Exceptions
{
    public class CannotHireException : Exception
    {
        public CannotHireException(string beneficiaryName)
            :base($"Le bénéficaire \"{beneficiaryName}\" ne peut être embauché, car il n'a pas postulé à ce poste !")
        {
        }
    }
}

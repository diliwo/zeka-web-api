﻿using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Core.Interfaces
{
    public interface IDocumentPartnerRepository
    {
        void Persist(DocumentPartner document);
        DocumentPartner Get(int id);
        IQueryable<DocumentPartner> GetDocuments();
        IQueryable<DocumentPartner> getDocumentsByJobIAndPartnerId(int partnerId, int jobId);
        void Delete(int id);
    }
}
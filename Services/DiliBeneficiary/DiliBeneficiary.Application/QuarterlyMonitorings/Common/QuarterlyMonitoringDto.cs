using AutoMapper;

namespace DiliBeneficiary.Application.QuarterlyMonitorings.Common
{
    public class QuarterlyMonitoringDto : IMapFrom<QuarterlyMonitoring>
    {
        public int QMonitoringId { get; set; }
        public int BeneficiaryId { get; set; }
        public string BeneficiaryName { get; set; }
        public string BeneficiaryLastName { get; set; }
        public string BeneficiaryFirstName { get; set; }
        public string BeneficiaryNiss { get; set; }
        public string BeneficiaryDossier { get; set; }
        public int ReferentId { get; set; }
        public string ReferentName { get; set; }
        public string ReferentLastName { get; set; }
        public string ReferentFirstName { get; set; }
        public int MonitoringActionId { get; set; }
        public string MonitoringActionLabel { get; set; }
        public DateTime ActionDate { get; set; }
        public string ActionComment { get; set; }
        public string Quarter { get; set; }
        public bool Softdelete { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<QuarterlyMonitoring, QuarterlyMonitoringDto>()
                .ForMember(d => d.QMonitoringId, 
                    opt => opt.MapFrom(q => q.Id))
                .ForMember(d => d.BeneficiaryId, 
                    opt => opt.MapFrom(q => q.BeneficiaryId))
                .ForMember(d => d.BeneficiaryName,
                    opt => opt.MapFrom(q => q.Beneficiary.FullName))
                .ForMember(d => d.BeneficiaryLastName,
                    opt => opt.MapFrom(q => q.Beneficiary.LastName))
                .ForMember(d => d.BeneficiaryFirstName,
                    opt => opt.MapFrom(q => q.Beneficiary.FirstName))
                .ForMember(d => d.BeneficiaryNiss,
                    opt => opt.MapFrom(q => q.Beneficiary.Niss))
                .ForMember(d => d.BeneficiaryDossier,
                    opt => opt.MapFrom(q => q.Beneficiary.ReferenceNumber))
                .ForMember(d => d.ReferentId,
                    opt => opt.MapFrom(q => q.ReferentId))
                .ForMember(d => d.ReferentName,
                    opt => opt.MapFrom(q => q.Referent.FullName))
                .ForMember(d => d.ReferentLastName,
                    opt => opt.MapFrom(q => q.Referent.LastName))
                .ForMember(d => d.ReferentFirstName,
                    opt => opt.MapFrom(q => q.Referent.FirstName))
                .ForMember(d => d.MonitoringActionId,
                    opt => opt.MapFrom(q => q.MonitoringActionId))
                .ForMember(d => d.MonitoringActionLabel,
                    opt => opt.MapFrom(q => q.MonitoringAction.Action))
                .ForMember(d => d.ActionDate,
                    opt => opt.MapFrom(q => q.ActionDate))
                .ForMember(d => d.ActionComment,
                    opt => opt.MapFrom(q => q.ActionComment))
                .ForMember(d => d.Quarter,
                    opt => opt.MapFrom(q => q.Quarter))
                .ForMember(d => d.Softdelete,
                    opt => opt.MapFrom(q => q.Softdelete));
        }
    }
}

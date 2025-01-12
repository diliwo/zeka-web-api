using AutoMapper;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Core.Entities;

namespace ClientManagement.Application.QuarterlyMonitorings.Common
{
    public class QuarterlyMonitoringDto : IMapFrom<MonitoringReport>
    {
        public int QMonitoringId { get; set; }
        public int ClientId { get; set; }
        public string ClientName { get; set; }
        public string ClientLastName { get; set; }
        public string ClientFirstName { get; set; }
        public string ClientNiss { get; set; }
        public string ClientDossier { get; set; }
        public int StaffMemberId { get; set; }
        public string StaffMemberName { get; set; }
        public string StaffMemberLastName { get; set; }
        public string StaffMemberFirstName { get; set; }
        public int MonitoringActionId { get; set; }
        public string MonitoringActionLabel { get; set; }
        public DateTime ActionDate { get; set; }
        public string ActionComment { get; set; }
        public string Quarter { get; set; }
        public bool Softdelete { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<MonitoringReport, QuarterlyMonitoringDto>()
                .ForMember(d => d.QMonitoringId, 
                    opt => opt.MapFrom(q => q.Id))
                .ForMember(d => d.ClientId, 
                    opt => opt.MapFrom(q => q.ClientId))
                .ForMember(d => d.ClientName,
                    opt => opt.MapFrom(q => q.Client.FullName))
                .ForMember(d => d.ClientLastName,
                    opt => opt.MapFrom(q => q.Client.LastName))
                .ForMember(d => d.ClientFirstName,
                    opt => opt.MapFrom(q => q.Client.FirstName))
                .ForMember(d => d.ClientNiss,
                    opt => opt.MapFrom(q => q.Client.Ssn))
                .ForMember(d => d.ClientDossier,
                    opt => opt.MapFrom(q => q.Client.ReferenceNumber))
                .ForMember(d => d.StaffMemberId,
                    opt => opt.MapFrom(q => q.SocialWorkerId))
                .ForMember(d => d.StaffMemberName,
                    opt => opt.MapFrom(q => q.SocialWorker.FullName))
                .ForMember(d => d.StaffMemberLastName,
                    opt => opt.MapFrom(q => q.SocialWorker.LastName))
                .ForMember(d => d.StaffMemberFirstName,
                    opt => opt.MapFrom(q => q.SocialWorker.FirstName))
                .ForMember(d => d.MonitoringActionId,
                    opt => opt.MapFrom(q => q.MonitoringActionId))
                .ForMember(d => d.MonitoringActionLabel,
                    opt => opt.MapFrom(q => q.MonitoringAction.Action))
                .ForMember(d => d.ActionDate,
                    opt => opt.MapFrom(q => q.ActionDate))
                .ForMember(d => d.ActionComment,
                    opt => opt.MapFrom(q => q.ActionComment))
                .ForMember(d => d.Quarter,
                    opt => opt.MapFrom(q => q.Period))
                .ForMember(d => d.Softdelete,
                    opt => opt.MapFrom(q => q.Softdelete));
        }
    }
}

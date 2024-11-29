using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Commun;
using AutoMapper;

namespace AdminAreaManagement.Application.Referents.Queries
{
    public class UserDto : IMapFrom<User>
    {
        public int IdUser { get; set; }
        public string UserName { get; set; }
        public string Lastname { get; set; }
        public string Firstname { get; set; }
        public bool SoftDeleted { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<User, UserDto>()
                .ForMember(u => u.IdUser, opt => opt.MapFrom(s => s.IdUser))
                .ForMember(u => u.UserName, opt => opt.MapFrom(s => s.UserName))
                .ForMember(u => u.Lastname, opt => opt.MapFrom(s => s.Lastname))
                .ForMember(u => u.Firstname, opt => opt.MapFrom(s => s.Firstname));
        }
    }
}

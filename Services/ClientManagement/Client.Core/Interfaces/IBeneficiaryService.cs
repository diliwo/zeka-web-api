namespace Client.Core.Interfaces;

public interface IClientService
{
    Task<int> Update(List<string> nisses);
    Task<int> UpSert(string niss);
}
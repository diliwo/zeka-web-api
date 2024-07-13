using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;

namespace DiliBeneficiary.API;

public class Startup
{
    public Startup(IConfiguration configuration, IWebHostEnvironment hostingEnvironment)
    {
        Configuration = configuration;
        HostingEnvironment = hostingEnvironment;
    }

    public IConfiguration Configuration { get; }
}
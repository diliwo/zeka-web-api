using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.Common.Authorization;
using Microsoft.Extensions.Configuration;

namespace ClientManagement.Infrastructure.Authorization;

// No production credential is provisioned by this issue. The closed action gate precedes this adapter.
public interface IActionAuthorityServiceCredential { string? BearerToken { get; } }

internal sealed class UnprovisionedActionAuthorityCredential : IActionAuthorityServiceCredential
{
    public string? BearerToken => null;
}

internal sealed class ActionAuthorityClient(IHttpClientFactory factory, IConfiguration configuration,
    IActionAuthorityServiceCredential credential, TimeProvider clock) : IActionAuthorityReads
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    // Owner-approved identifier policy. This pins names, not platform transition rules.
    private const string IanaPolicyVersion = "Zeka canonical IANA IDs, policy v1 / tzdb 2026e";
    private const string IanaSourceVersion = "2026e";
    private const string IanaSourceArchiveSha256 =
        "b26882805f26aac59d5b222978e6580484b834ccdc98be89df2f05a6dc53a652";
    private const string IanaManifestSha256 =
        "6916cb132692f2b2c990b3b8da7ead8819e3ca606acc9b1a093143f80c7c733d";
    private const string CanonicalIanaManifest = """
        Africa/Abidjan
        Africa/Algiers
        Africa/Bissau
        Africa/Cairo
        Africa/Casablanca
        Africa/Ceuta
        Africa/El_Aaiun
        Africa/Johannesburg
        Africa/Juba
        Africa/Khartoum
        Africa/Lagos
        Africa/Maputo
        Africa/Monrovia
        Africa/Nairobi
        Africa/Ndjamena
        Africa/Sao_Tome
        Africa/Tripoli
        Africa/Tunis
        Africa/Windhoek
        America/Adak
        America/Anchorage
        America/Araguaina
        America/Argentina/Buenos_Aires
        America/Argentina/Catamarca
        America/Argentina/Cordoba
        America/Argentina/Jujuy
        America/Argentina/La_Rioja
        America/Argentina/Mendoza
        America/Argentina/Rio_Gallegos
        America/Argentina/Salta
        America/Argentina/San_Juan
        America/Argentina/San_Luis
        America/Argentina/Tucuman
        America/Argentina/Ushuaia
        America/Asuncion
        America/Bahia
        America/Bahia_Banderas
        America/Barbados
        America/Belem
        America/Belize
        America/Boa_Vista
        America/Bogota
        America/Boise
        America/Cambridge_Bay
        America/Campo_Grande
        America/Cancun
        America/Caracas
        America/Cayenne
        America/Chicago
        America/Chihuahua
        America/Ciudad_Juarez
        America/Costa_Rica
        America/Coyhaique
        America/Cuiaba
        America/Danmarkshavn
        America/Dawson
        America/Dawson_Creek
        America/Denver
        America/Detroit
        America/Edmonton
        America/Eirunepe
        America/El_Salvador
        America/Fort_Nelson
        America/Fortaleza
        America/Glace_Bay
        America/Goose_Bay
        America/Grand_Turk
        America/Guatemala
        America/Guayaquil
        America/Guyana
        America/Halifax
        America/Havana
        America/Hermosillo
        America/Indiana/Indianapolis
        America/Indiana/Knox
        America/Indiana/Marengo
        America/Indiana/Petersburg
        America/Indiana/Tell_City
        America/Indiana/Vevay
        America/Indiana/Vincennes
        America/Indiana/Winamac
        America/Inuvik
        America/Iqaluit
        America/Jamaica
        America/Juneau
        America/Kentucky/Louisville
        America/Kentucky/Monticello
        America/La_Paz
        America/Lima
        America/Los_Angeles
        America/Maceio
        America/Managua
        America/Manaus
        America/Martinique
        America/Matamoros
        America/Mazatlan
        America/Menominee
        America/Merida
        America/Metlakatla
        America/Mexico_City
        America/Miquelon
        America/Moncton
        America/Monterrey
        America/Montevideo
        America/New_York
        America/Nome
        America/Noronha
        America/North_Dakota/Beulah
        America/North_Dakota/Center
        America/North_Dakota/New_Salem
        America/Nuuk
        America/Ojinaga
        America/Panama
        America/Paramaribo
        America/Phoenix
        America/Port-au-Prince
        America/Porto_Velho
        America/Puerto_Rico
        America/Punta_Arenas
        America/Rankin_Inlet
        America/Recife
        America/Regina
        America/Resolute
        America/Rio_Branco
        America/Santarem
        America/Santiago
        America/Santo_Domingo
        America/Sao_Paulo
        America/Scoresbysund
        America/Sitka
        America/St_Johns
        America/Swift_Current
        America/Tegucigalpa
        America/Thule
        America/Tijuana
        America/Toronto
        America/Vancouver
        America/Whitehorse
        America/Winnipeg
        America/Yakutat
        Antarctica/Casey
        Antarctica/Davis
        Antarctica/Macquarie
        Antarctica/Mawson
        Antarctica/Palmer
        Antarctica/Rothera
        Antarctica/Troll
        Antarctica/Vostok
        Asia/Almaty
        Asia/Amman
        Asia/Anadyr
        Asia/Aqtau
        Asia/Aqtobe
        Asia/Ashgabat
        Asia/Atyrau
        Asia/Baghdad
        Asia/Baku
        Asia/Bangkok
        Asia/Barnaul
        Asia/Beirut
        Asia/Bishkek
        Asia/Chita
        Asia/Colombo
        Asia/Damascus
        Asia/Dhaka
        Asia/Dili
        Asia/Dubai
        Asia/Dushanbe
        Asia/Famagusta
        Asia/Gaza
        Asia/Hebron
        Asia/Ho_Chi_Minh
        Asia/Hong_Kong
        Asia/Hovd
        Asia/Irkutsk
        Asia/Jakarta
        Asia/Jayapura
        Asia/Jerusalem
        Asia/Kabul
        Asia/Kamchatka
        Asia/Karachi
        Asia/Kathmandu
        Asia/Khandyga
        Asia/Kolkata
        Asia/Krasnoyarsk
        Asia/Kuching
        Asia/Macau
        Asia/Magadan
        Asia/Makassar
        Asia/Manila
        Asia/Nicosia
        Asia/Novokuznetsk
        Asia/Novosibirsk
        Asia/Omsk
        Asia/Oral
        Asia/Pontianak
        Asia/Pyongyang
        Asia/Qatar
        Asia/Qostanay
        Asia/Qyzylorda
        Asia/Riyadh
        Asia/Sakhalin
        Asia/Samarkand
        Asia/Seoul
        Asia/Shanghai
        Asia/Singapore
        Asia/Srednekolymsk
        Asia/Taipei
        Asia/Tashkent
        Asia/Tbilisi
        Asia/Tehran
        Asia/Thimphu
        Asia/Tokyo
        Asia/Tomsk
        Asia/Ulaanbaatar
        Asia/Urumqi
        Asia/Ust-Nera
        Asia/Vladivostok
        Asia/Yakutsk
        Asia/Yangon
        Asia/Yekaterinburg
        Asia/Yerevan
        Atlantic/Azores
        Atlantic/Bermuda
        Atlantic/Canary
        Atlantic/Cape_Verde
        Atlantic/Faroe
        Atlantic/Madeira
        Atlantic/South_Georgia
        Atlantic/Stanley
        Australia/Adelaide
        Australia/Brisbane
        Australia/Broken_Hill
        Australia/Darwin
        Australia/Eucla
        Australia/Hobart
        Australia/Lindeman
        Australia/Lord_Howe
        Australia/Melbourne
        Australia/Perth
        Australia/Sydney
        Etc/GMT
        Etc/GMT+1
        Etc/GMT+10
        Etc/GMT+11
        Etc/GMT+12
        Etc/GMT+2
        Etc/GMT+3
        Etc/GMT+4
        Etc/GMT+5
        Etc/GMT+6
        Etc/GMT+7
        Etc/GMT+8
        Etc/GMT+9
        Etc/GMT-1
        Etc/GMT-10
        Etc/GMT-11
        Etc/GMT-12
        Etc/GMT-13
        Etc/GMT-14
        Etc/GMT-2
        Etc/GMT-3
        Etc/GMT-4
        Etc/GMT-5
        Etc/GMT-6
        Etc/GMT-7
        Etc/GMT-8
        Etc/GMT-9
        Etc/UTC
        Europe/Andorra
        Europe/Astrakhan
        Europe/Athens
        Europe/Belgrade
        Europe/Berlin
        Europe/Brussels
        Europe/Bucharest
        Europe/Budapest
        Europe/Chisinau
        Europe/Dublin
        Europe/Gibraltar
        Europe/Helsinki
        Europe/Istanbul
        Europe/Kaliningrad
        Europe/Kirov
        Europe/Kyiv
        Europe/Lisbon
        Europe/London
        Europe/Madrid
        Europe/Malta
        Europe/Minsk
        Europe/Moscow
        Europe/Paris
        Europe/Prague
        Europe/Riga
        Europe/Rome
        Europe/Samara
        Europe/Saratov
        Europe/Simferopol
        Europe/Sofia
        Europe/Tallinn
        Europe/Tirane
        Europe/Ulyanovsk
        Europe/Vienna
        Europe/Vilnius
        Europe/Volgograd
        Europe/Warsaw
        Europe/Zurich
        Indian/Chagos
        Indian/Maldives
        Indian/Mauritius
        Pacific/Apia
        Pacific/Auckland
        Pacific/Bougainville
        Pacific/Chatham
        Pacific/Easter
        Pacific/Efate
        Pacific/Fakaofo
        Pacific/Fiji
        Pacific/Galapagos
        Pacific/Gambier
        Pacific/Guadalcanal
        Pacific/Guam
        Pacific/Honolulu
        Pacific/Kanton
        Pacific/Kiritimati
        Pacific/Kosrae
        Pacific/Kwajalein
        Pacific/Marquesas
        Pacific/Nauru
        Pacific/Niue
        Pacific/Norfolk
        Pacific/Noumea
        Pacific/Pago_Pago
        Pacific/Palau
        Pacific/Pitcairn
        Pacific/Port_Moresby
        Pacific/Rarotonga
        Pacific/Tahiti
        Pacific/Tarawa
        Pacific/Tongatapu
        """ + "\n";
    private static readonly FrozenSet<string> CanonicalIanaIds =
        CanonicalIanaManifest.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToFrozenSet(StringComparer.Ordinal);

    public async Task<ActionTimeZone> TimeZoneAsync(Guid organisationId, Guid actorSubjectId,
        Guid actorMembershipId, CancellationToken cancellationToken)
    {
        var endpoint = Endpoint("organisation-time-zone");
        var token = Token();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var request = NewRequest(endpoint, token,
                new { contractVersion = 1, organisationId, actorSubjectId, actorMembershipId });
            using var response = await factory.CreateClient("ActionAuthority").SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK) throw Unavailable();
            var result = await response.Content.ReadFromJsonAsync<TimeZoneWire>(WireOptions, deadline.Token);
            if (result is null || result.ContractVersion != 1 || result.OrganisationId != organisationId
                || result.ConfigVersion <= 0 || result.ObservedAtUtc == default
                || !Fresh(result.ObservedAtUtc, clock.GetUtcNow())
                || !IanaZone(result.TimeZoneId, out var zone)) throw Unavailable();
            return new ActionTimeZone(result.TimeZoneId!, result.ConfigVersion,
                result.ObservedAtUtc, zone!);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Unavailable(); }
        catch (HttpRequestException) { throw Unavailable(); }
        catch (JsonException) { throw Unavailable(); }
        catch (NotSupportedException) { throw Unavailable(); }
    }

    public async Task<AssigneeDecision?> EligibilityAsync(Guid organisationId, Guid actorSubjectId,
        Guid actorMembershipId, Guid targetMembershipId, CancellationToken cancellationToken)
    {
        var endpoint = Endpoint("assignee-eligibility");
        var token = Token();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var request = NewRequest(endpoint, token,
                    new { contractVersion = 1, organisationId, actorSubjectId, actorMembershipId, targetMembershipId });
                try
                {
                    using var response = await factory.CreateClient("ActionAuthority").SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var result = await response.Content.ReadFromJsonAsync<EligibilityWire>(WireOptions, deadline.Token);
                        if (result is null || result.ContractVersion != 1 || result.Eligible is null)
                            throw Unavailable();
                        if (result.Eligible == false)
                        {
                            if (result.OrganisationId is not null || result.ActorMembershipId is not null
                                || result.TargetMembershipId is not null || result.TargetViewScope is not null
                                || result.DecisionVersion is not null || result.ObservedAtUtc is not null)
                                throw Unavailable();
                            return null;
                        }
                        if (result.OrganisationId != organisationId || result.ActorMembershipId != actorMembershipId
                            || result.TargetMembershipId != targetMembershipId
                            || result.TargetViewScope is not ("All" or "Assigned")
                            || string.IsNullOrWhiteSpace(result.DecisionVersion) || result.DecisionVersion.Length > 256
                            || result.ObservedAtUtc is not { } observed || !Fresh(observed, clock.GetUtcNow()))
                            throw Unavailable();
                        return new AssigneeDecision(result.TargetViewScope, result.DecisionVersion, observed);
                    }
                    if (!Transient(response.StatusCode) || attempt == 2) throw Unavailable();
                }
                catch (HttpRequestException) when (attempt == 1) { }
                if (attempt == 1) await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
            }
            throw Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Unavailable(); }
        catch (HttpRequestException) { throw Unavailable(); }
        catch (JsonException) { throw Unavailable(); }
        catch (NotSupportedException) { throw Unavailable(); }
    }

    private Uri Endpoint(string purpose)
    {
        var address = configuration["ActionAuthority:AuthManagementUrl"];
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)) throw Unavailable();
        return new Uri(uri, $"/internal/v1/{purpose}");
    }

    private string Token() => string.IsNullOrWhiteSpace(credential.BearerToken)
        ? throw Unavailable() : credential.BearerToken!;

    private static HttpRequestMessage NewRequest(Uri endpoint, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static bool Transient(HttpStatusCode code) => code is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests || (int)code >= 500;

    private static bool Fresh(DateTimeOffset observed, DateTimeOffset now) =>
        observed.Offset == TimeSpan.Zero && now - observed is var age
        && age >= TimeSpan.FromSeconds(-30) && age <= TimeSpan.FromSeconds(2);

    private static bool IsCanonicalIanaId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 128
        && id.Contains('/', StringComparison.Ordinal) && CanonicalIanaIds.Contains(id);

    private static bool IanaZone(string? id, out TimeZoneInfo? zone)
    {
        zone = null;
        if (!IsCanonicalIanaId(id)) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id!);
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static TenantAccessException Unavailable() => new(AccessFailure.Unavailable);

    private sealed record TimeZoneWire(int ContractVersion, Guid OrganisationId, string? TimeZoneId,
        long ConfigVersion, DateTimeOffset ObservedAtUtc);

    private sealed record EligibilityWire(int ContractVersion, bool? Eligible, Guid? OrganisationId,
        Guid? ActorMembershipId, Guid? TargetMembershipId, string? TargetViewScope,
        string? DecisionVersion, DateTimeOffset? ObservedAtUtc);
}

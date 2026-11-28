# Ocelot API Gateway

This directory contains Zeka’s ASP.NET Core gateway. Ocelot matches incoming HTTP methods and paths and forwards the requests to downstream services, including Auth, Client and AdminArea.

The browser transport covers sign-in, session restore and logout, registration and email verification, and organisation and tenant access. It checks the configured HTTPS origins and trusted peers, rejects caller-supplied forwarding headers, and validates the browser route table before enabling it. In the `LocalStack` environment, `/health/live` reports liveness and `/health/ready` checks the browser transport configuration.

## Files

| File | Purpose |
| --- | --- |
| [`Program.cs`](Program.cs) | Starts the gateway, wires JWT authentication and Ocelot into the request pipeline, and enables Swagger in Development. Also defines the browser transport checks, filtered request logging, and LocalStack health endpoints. |
| [`ocelot.json`](ocelot.json) | Maps upstream HTTP methods and paths to downstream routes. Defines browser routes and legacy service routes, with per-route caching and rate limits where configured. |
| [`appsettings.json`](appsettings.json) | Defines the base ASP.NET Core logging levels and host-filtering defaults. |
| [`appsettings.Development.json`](appsettings.Development.json) | Supplies logging settings for the Development environment. |
| [`Ocelot.ApiGateway.csproj`](Ocelot.ApiGateway.csproj) | Defines the .NET 8 web project, its Ocelot, Swagger and authentication dependencies, and the Docker build context. |
| [`packages.lock.json`](packages.lock.json) | Pins direct and transitive NuGet package versions and their content hashes for strict locked restore. |
| [`Dockerfile`](Dockerfile) | Builds and publishes the gateway from the repository root using strict locked restore and a BuildKit package credential. The final image runs as a non-root user and exposes port 8080. |
| [`Properties/launchSettings.json`](Properties/launchSettings.json) | Defines the local Development profile at `http://localhost:5100` and the Visual Studio Docker profile. |
| [`Ocelot.ApiGateway.http`](Ocelot.ApiGateway.http) | Contains the ASP.NET starter HTTP request for `GET /weatherforecast/` at `http://localhost:9011`. |

`bin/` and `obj/` are generated build output directories.

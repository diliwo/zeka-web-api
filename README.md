# Zeka™ Web Api

Zeka is an open source micro SaaS application.

The API for Zeka is designed to handle client and case management via a scalable microservices architecture.

## Tech stack

- <strong>.NET Core</strong> - .NET Framework and .NET Core, including ASP.NET and ASP.NET Core
- <strong>IdentityModel.Tokens</strong>  - Responsible for handling token-related operations for .NET Core
- <strong>Authentication.JwtBearer</strong> - Integrates JWT authentication directly into the ASP.NET Core middleware pipeline
- <strong>Ocelot</strong> - A toolkit for developing high-performance HTTP reverse proxy applications
- <strong>FluentValidation</strong> - Popular .NET validation library for building strongly-typed validation rules
- <strong>MediatR</strong> - Simple, unambitious mediator implementation in .NET
- <strong>EF Core</strong> - Modern object-database mapper for .NET. It supports LINQ queries, change tracking, updates, and schema migrations
- <strong>Serilog</strong> - Simple .NET logging with fully-structured events
- <strong>helm</strong> - Best package manager to find, share, and use software built for Kubernetes
- <strong>Kubernetes / AKS</strong> - The app is designed to run on Kubernetes (both locally on "Rancher Desktop" as well as on the cloud with AKS)
- <strong>Istio</strong> - An open-source service mesh that provides traffic management, security, and observability for microservices in a Kubernetes environment.
- <strong>RabbitMQ</strong> - An open-source message broker that enables applications to communicate asynchronously by sending and receiving messages through queues.
- <strong>PostgreSQL</strong> - A powerful open-source relational database system known for its reliability, feature richness, and support for complex queries and data types.
- <strong>Ocelot</strong> - An open-source API gateway for .NET that routes requests, handles authentication, and manages cross-cutting concerns in microservice architectures.  

## Architecture

![Zeka_archi](https://github.com/user-attachments/assets/30ff9b7a-72d5-4766-a4fe-3d37f1f94fff)

## Private package restore

`Zeka.Extensions.*` packages are restored from GitHub Packages. The tracked
`nuget.config` contains package sources only; credentials must be injected at
runtime through 1Password.

See [Private package restore](Docs/security/private-package-restore.md) for the
one-time setup and restore command. Before committing package configuration,
run `./scripts/security/check-nuget-credentials.sh`.

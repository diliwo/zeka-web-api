# Zeka™ Web Api

Zeka is an open source micro SaaS application.

The API for Zeka is designed to handle client and case management via a scalable microservices architecture.

## Tech stack

- .NET Core - .NET Framework and .NET Core, including ASP.NET and ASP.NET Core
- IdentityModel.Tokens  - Responsible for handling token-related operations for .NET Core
- Authentication.JwtBearer - Integrates JWT authentication directly into the ASP.NET Core middleware pipeline
- Ocelot - A toolkit for developing high-performance HTTP reverse proxy applications
- FluentValidation - Popular .NET validation library for building strongly-typed validation rules
- MediatR - Simple, unambitious mediator implementation in .NET
- EF Core - Modern object-database mapper for .NET. It supports LINQ queries, change tracking, updates, and schema migrations
- Serilog - Simple .NET logging with fully-structured events
- helm - Best package manager to find, share, and use software built for Kubernetes
- Kubernetes / AKS - The app is designed to run on Kubernetes (both locally on "Rancher Desktop" as well as on the cloud with AKS)
- Istio - An open-source service mesh that provides traffic management, security, and observability for microservices in a Kubernetes environment.
- RabbitMQ - An open-source message broker that enables applications to communicate asynchronously by sending and receiving messages through queues.
- PostgreSQL - A powerful open-source relational database system known for its reliability, feature richness, and support for complex queries and data types.
- Ocelot - An open-source API gateway for .NET that routes requests, handles authentication, and manages cross-cutting concerns in microservice architectures.  

## Architecture

![Zeka_archi](https://github.com/user-attachments/assets/30ff9b7a-72d5-4766-a4fe-3d37f1f94fff)

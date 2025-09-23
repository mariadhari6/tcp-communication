# TCP Communication Project

This repository contains two .NET 8.0 console applications for TCP communication:

- **client**: Sends messages to a TCP server.
- **listener**: Receives messages from a TCP client.

## Structure

- [`client/`](client): TCP client application.
- [`listener/`](listener): TCP server (listener) application.

Each project contains:
- Source code (`Program.cs`)
- Project files (`.csproj`, `.sln`)
- Environment configuration (`.env`, `.env_example`)
- Build output (`bin/`, `obj/`)
- Logs (`logs/`)

## Getting Started

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Configuration

Copy `.env_example` to `.env` in both [`client/`](client) and [`listener/`](listener) folders and adjust settings as needed.

### Build

From the root directory, build both projects:

```sh
dotnet build client/client.csproj
dotnet build listener/listener.csproj
```

### Run

Start the listener first:

```sh
dotnet run --project listener/listener.csproj
```

Then start the client:

```sh
dotnet run --project client/client.csproj
```

## Logging

Logs are written to the `logs/` directory in each project.

## Environment Variables

Configure TCP host, port, and simulation mode in `.env` files.

## Dependencies

- [DotNetEnv](https://www.nuget.org/packages/DotNetEnv)
- [Serilog](https://www.nuget.org/packages/Serilog)
- [Serilog.Sinks.Console](https://www.nuget.org/packages/Serilog.Sinks.Console)
- [Serilog.Sinks.File](https://www.nuget.org/packages/Serilog.Sinks.File)
- [Sprache](https://www.nuget.org/packages/Sprache)

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

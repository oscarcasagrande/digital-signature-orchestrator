FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props DigitalSignature.sln ./
COPY src/ src/
RUN dotnet publish src/Orchestrator.Worker -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Orchestrator.Worker.dll"]

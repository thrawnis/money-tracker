# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY MoneyTracker.Api.csproj .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
# 7-Zip for the daily encrypted backups (UserBackupService).
RUN apt-get update && apt-get install -y --no-install-recommends 7zip && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MoneyTracker.Api.dll"]

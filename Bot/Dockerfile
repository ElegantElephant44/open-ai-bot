﻿FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet restore "Bot.fsproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "Bot.fsproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Bot.fsproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bot.dll"]

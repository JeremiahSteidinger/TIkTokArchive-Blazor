# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["TikTokArchive.Web/TikTokArchive.Web.csproj", "TikTokArchive.Web/"]
COPY ["TikTokArchive.Entities/TikTokArchive.Entities.csproj", "TikTokArchive.Entities/"]
RUN dotnet restore "./TikTokArchive.Web/TikTokArchive.Web.csproj"
COPY . .
WORKDIR "/src/TikTokArchive.Web"
RUN dotnet build "./TikTokArchive.Web.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./TikTokArchive.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Make yt-dlp executable
RUN chmod 755 /app/Tools/yt-dlp

RUN mkdir -p /app/data \
    && chown -R $APP_UID:$APP_UID /app/data \
    && chmod 700 /app/data

USER $APP_UID

ENTRYPOINT ["dotnet", "TikTokArchive.Web.dll"]
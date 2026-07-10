# Multi-stage build for the C# port of vision-api.
# Produces a self-contained ASP.NET Core service on the same 4000 port and
# /healthcheck.json contract as the Node image, so it is a drop-in swap in the
# vision_vapi stack.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY EcencyApi/EcencyApi.csproj EcencyApi/
RUN dotnet restore EcencyApi/EcencyApi.csproj

COPY EcencyApi/ EcencyApi/
RUN dotnet publish EcencyApi/EcencyApi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Static assets served by express.static in the Node version (public/).
COPY EcencyApi/public/ ./public/
COPY --from=build /app ./

ENV API_PORT=4000
ENV ASPNETCORE_URLS=
EXPOSE 4000

# Same health contract the Node healthCheck.js polled.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["dotnet", "EcencyApi.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "EcencyApi.dll"]

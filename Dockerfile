FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/RelayHub/RelayHub.csproj src/RelayHub/
RUN dotnet restore src/RelayHub/RelayHub.csproj
COPY src/ src/
RUN dotnet publish src/RelayHub/RelayHub.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__RelayDb="Data Source=/data/relayhub.db"
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "RelayHub.dll"]

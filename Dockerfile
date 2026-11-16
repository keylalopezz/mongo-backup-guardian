# Etapa 1: compilación con el SDK de .NET 10
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Guardian/Guardian.csproj src/Guardian/
RUN dotnet restore src/Guardian/Guardian.csproj
COPY src/Guardian/ src/Guardian/
RUN dotnet publish src/Guardian/Guardian.csproj -c Release -o /app/publish /p:UseAppHost=false

# Etapa 2: imagen final solo con el runtime de ASP.NET Core
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ARG GIT_SHA=local
ENV APP_VERSION=$GIT_SHA \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Guardian.dll"]

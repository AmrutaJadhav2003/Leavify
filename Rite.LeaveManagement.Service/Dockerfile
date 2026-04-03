# ---------- Build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Rite.LeaveManagement.Svc/*.csproj Rite.LeaveManagement.Svc/
RUN dotnet restore Rite.LeaveManagement.Svc/Rite.LeaveManagement.Svc.csproj

COPY . .
RUN dotnet publish Rite.LeaveManagement.Svc/Rite.LeaveManagement.Svc.csproj -c Release -o /app/publish

# ---------- Runtime stage ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Force HTTPS 5050
ENV ASPNETCORE_URLS=https://+:5050
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5050

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Rite.LeaveManagement.Svc.dll"]

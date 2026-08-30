# ------------------------------------------------------------
# HabitTracker - Cloud Run 用 Dockerfile (.NET 10 対応)
# ------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["HabitTracker.csproj", "./"]
RUN dotnet restore "HabitTracker.csproj"

COPY . .
RUN dotnet publish "HabitTracker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV DB_DIR=/app/data
ENV PORT=8080
EXPOSE 8080

RUN mkdir -p /app/data

ENTRYPOINT ["dotnet", "HabitTracker.dll"]

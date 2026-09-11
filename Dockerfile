FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/LanTodo.Core/ src/LanTodo.Core/
COPY src/LanTodo.Nas/ src/LanTodo.Nas/
RUN dotnet publish src/LanTodo.Nas -c Release -a $TARGETARCH --self-contained false -p:UseAppHost=false -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
LABEL org.opencontainers.image.title="LanTodo NAS" org.opencontainers.image.version="1.0.1" org.opencontainers.image.licenses="GPL-3.0-only"
WORKDIR /app
COPY --from=build /out/ ./
COPY LICENSE THIRD_PARTY_NOTICES.md ./
COPY docs/licenses/ ./docs/licenses/
RUN mkdir /data /backups && chown app:app /data /backups
USER app
ENV LANTODO_DATA=/data LANTODO_BACKUPS=/backups LANTODO_NAME="LanTodo NAS" LANTODO_WEB_PORT=42000
VOLUME ["/data", "/backups"]
EXPOSE 42851/tcp
EXPOSE 42000/tcp
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 CMD ["dotnet", "LanTodo.Nas.dll", "status"]
ENTRYPOINT ["dotnet", "LanTodo.Nas.dll"]
CMD ["serve"]

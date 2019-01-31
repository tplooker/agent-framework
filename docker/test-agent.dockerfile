FROM streetcred/dotnet-indy:1.8.0-preview.1
WORKDIR /app

COPY . .
RUN dotnet restore "test/AgentFramework.Core.Tests/AgentFramework.Core.Tests.csproj"

COPY docker/docker_pool_genesis.txn test/AgentFramework.Core.Tests/pool_genesis.txn

WORKDIR /app/test/AgentFramework.Core.Tests

ENTRYPOINT ["dotnet", "test", "--verbosity", "normal"]
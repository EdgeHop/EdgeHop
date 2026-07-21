# EdgeHop MCP server — container image.
#
# Primary purpose: let registries (e.g. Glama) build the image and run an MCP
# introspection (tools/list) request against the stdio server to verify it starts.
# `edgehop mcp` registers its five tools at startup and resolves the target repo
# per call, so introspection succeeds without a repo mounted.
#
# To actually index/query in a container, mount your repo and point EDGEHOP_REPO
# at it, e.g.:
#   docker run -i --rm -v /path/to/repo:/work -e EDGEHOP_REPO=/work edgehop
# (The linux-x64 native oxc binary ships in the tool package, so JS/TS extraction
# works here too; C#/Razor extraction additionally needs a restored solution.)

# SDK image: `dotnet tool install` needs the SDK, not just the runtime.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# Install the published global tool from nuget.org. --prerelease grabs the current
# alpha line; pin a specific version for reproducible builds, e.g.
#   RUN dotnet tool install --global EdgeHop --version 0.1.23-alpha
RUN dotnet tool install --global EdgeHop --prerelease

# Global tools install under $HOME/.dotnet/tools (HOME=/root in this image).
ENV PATH="${PATH}:/root/.dotnet/tools"

# A neutral working dir a mounted repo can map onto.
WORKDIR /work

# The MCP server speaks JSON-RPC over stdio; keep stdin open when running (docker -i).
ENTRYPOINT ["edgehop", "mcp"]

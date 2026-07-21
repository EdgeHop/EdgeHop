# EdgeHop README demo — regenerate with:  vhs Assets/demo.tape
#
# Produces Assets/Images/demo.gif: a Blazor UI handler traced across the HTTP
# boundary to its API endpoint, on the public sample app.
#
# Runs in bash (VHS's native shell — pwsh + PSReadLine garbles under VHS's
# headless terminal). Prereqs (run VHS from the repo root; WSL with .NET 10 works):
#   - ffmpeg + ttyd + a headless-chromium lib set (VHS deps).
#   - jq on PATH:  sudo apt install jq
#   - EdgeHop on PATH:  dotnet tool install -g EdgeHop --prerelease
#   - The sample restored for THIS OS once (obj/ is OS-specific):
#       cd tests/samples/EdgeHopExplorer.BlazorServer && dotnet restore
# Tune the Sleep values to your machine (index ~5s in WSL) before committing the GIF.

Output Assets/Images/demo.gif

Require edgehop

Set Shell "bash"
Set FontSize 18
Set Width 1200
Set Height 640
Set Padding 20
Set Theme "Catppuccin Mocha"
Set TypingSpeed 90ms

# --- silent: minimal prompt + cd into the sample ---
Hide
Sleep 2s
Type "export PS1='$ '" Enter
Type "cd tests/samples/EdgeHopExplorer.BlazorServer" Enter
Type "clear" Enter
Show

# --- beat 1: index a Blazor Server + minimal-API solution into a code graph ---
Type "edgehop index EdgeHopExplorer.BlazorServer.sln" Sleep 500ms Enter
Sleep 12s

# blank lines (CRLF) for separation before the next beat
Enter
Enter
Enter
Sleep 1500ms

# --- silent: resolve the two symbol ids now that the store is populated ---
Hide
Type "ui=$(edgehop find-symbol OnInitializedAsync --json 2>/dev/null | jq -r '.hits[0].id')" Enter
Sleep 1s
Type "api=$(edgehop find-symbol MapFeatureEndpoints --json 2>/dev/null | jq -r '.hits[0].id')" Enter
Sleep 1s
Show

# --- beat 2: find a Blazor component's lifecycle handler (lives in a .razor file) ---
Type "edgehop find-symbol OnInitializedAsync" Sleep 500ms Enter
Sleep 10s

# blank lines (CRLF) for separation before the climax
Enter
Enter
Enter
Sleep 1500ms

# --- beat 3: trace the impact path to the API endpoint, across the HTTP boundary ---
Type 'edgehop get-path "$ui" "$api"' Sleep 500ms Enter
Sleep 14s

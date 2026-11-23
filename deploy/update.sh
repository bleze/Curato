#!/bin/sh
# Updates Curato on a Synology NAS: pulls the newest image and restarts the container if it
# changed. Container Manager does not check ghcr.io images for updates, so run this instead:
#
#   Control Panel -> Task Scheduler -> Create -> Scheduled Task -> User-defined script
#     General:  Task "curato update", User "root" (only root may use Docker on DSM)
#     Schedule: daily, e.g. 04:00 (or leave it disabled and use "Run" when you want to update)
#     Task Settings -> Run command:
#       sh /volume1/docker/curato/update.sh
#
# Copy this file to /volume1/docker/curato/ (the project folder, next to the compose file).
# Curato's settings live there and your libraries in your photo folders, so an update never
# touches either. To see this script's output: Task Scheduler -> Settings -> "Save output results".

set -u
# Task Scheduler runs with a minimal PATH; Synology keeps docker in /usr/local/bin.
PATH="/usr/local/bin:/usr/local/sbin:/usr/bin:/usr/sbin:/bin:/sbin:${PATH:-}"
export PATH

fail() { echo "curato update FAILED: $*"; exit 1; }

PROJECT_DIR="${PROJECT_DIR:-$(cd "$(dirname "$0")" && pwd)}"
cd "$PROJECT_DIR" || fail "folder $PROJECT_DIR not found"
echo "curato update in $PROJECT_DIR ($(date '+%Y-%m-%d %H:%M'))"

[ "$(id -u)" = 0 ] || fail "runs as $(id -un), but only root may use Docker: Task Scheduler -> Edit -> General -> User: root"
command -v docker >/dev/null 2>&1 || fail "docker not found (is Container Manager installed?)"
if docker compose version >/dev/null 2>&1; then
    DC="docker compose"
elif command -v docker-compose >/dev/null 2>&1; then
    DC="docker-compose"
else
    fail "docker compose not found"
fi
COMPOSE_FILE=""
for f in compose.yaml compose.yml docker-compose.yaml docker-compose.yml; do
    [ -f "$f" ] && { COMPOSE_FILE="$f"; break; }
done
[ -n "$COMPOSE_FILE" ] ||
    fail "no compose file in $PROJECT_DIR (it has: $(ls | xargs)) - copy this script into the Container Manager project folder"
echo "using: $DC with $COMPOSE_FILE"

IMAGE=$(docker inspect --format '{{.Config.Image}}' curato 2>/dev/null || echo "ghcr.io/bleze/curato:latest")
running=$(docker inspect --format '{{.Image}}' curato 2>/dev/null || echo none)
echo "pulling the newest image..."
$DC pull curato || fail "pull failed (internet? ghcr.io reachable?)"
newest=$(docker image inspect --format '{{.Id}}' "$IMAGE" 2>/dev/null || echo unknown)

if [ "$running" = "$newest" ]; then
    echo "curato is up to date"
    exit 0
fi

# Stop it properly first so a running scan or iCloud sync can finish its current step.
# Letting Docker replace it directly allows only 10 s before a forced kill.
if [ "$running" != none ]; then
    echo "stopping curato (up to 60 s)..."
    $DC stop -t 60 curato || fail "stop failed"
    echo "old version exited with code $(docker inspect --format '{{.State.ExitCode}}' curato 2>/dev/null || echo '?') (0 = clean stop)"
fi
echo "starting the new version..."
$DC up -d curato || fail "start failed"
echo "curato updated and restarted"
docker image prune -f >/dev/null 2>&1  # remove the replaced image

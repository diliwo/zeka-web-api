#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

readonly PROJECT_MANIFEST=scripts/ci/strict-restore-projects.tsv
readonly LOCK_MANIFEST=scripts/ci/strict-restore-locks.sha256
readonly STRICT_CONFIG=scripts/ci/nuget.strict.config
readonly CONFIG_HASH=840677413139f10e5591265935471a49c33b8862e1b05f608d5e689964d2f917
readonly ACCEPTED_BASE=97a133bca86cb86cd9cb0e04a9b29e55331beb02
readonly ACCEPTED_SOURCE=07e7c8c5f253eddecc538ca1e64062cb2972d633
readonly ACCEPTED_OVERLAY=2623b4f697ad64c5ee6ac59493b6b4ab0d3b1d10
readonly SDK_VERSION=8.0.129
readonly GITHUB_FEED=https://nuget.pkg.github.com/diliwo/index.json
readonly PUBLIC_FEED=https://api.nuget.org/v3/index.json
readonly HELPER=Tools/Zeka.AuthOwnerProvision/Zeka.AuthOwnerProvision.csproj
declare -A DOCKER_ROOT=(
    [admin]=Services/AdminAreaManagement/AdminAreaManagement.API/AdminAreaManagement.API.csproj
    [client]=Services/ClientManagement/Client.API/ClientManagement.API.csproj
    [auth]=Services/AuthManager/AuthManager.API/AuthManager.API.csproj
    [gateway]=ApiGateways/Ocelot.ApiGateway/Ocelot.ApiGateway.csproj
)
declare -a PROJECTS=() ALL_LOCKS=() SELECTED_PROJECTS=() SELECTED_LOCKS=()
declare -A PROJECT_KEYS=() PROJECT_RIDS=() LOCK_HASH=()

fail() {
    printf 'strict-restore: %s\n' "$*" >&2
    exit 1
}

require_sha() { [[ $1 =~ ^[0-9a-f]{40}$ ]] || fail 'expected a full Git object ID'; }
is_regular() { [[ -f $1 && ! -L $1 ]]; }
hash_file() { sha256sum -- "$1" | cut -c1-64; }
portable_lock() { printf '%s/packages.lock.json' "${1%/*}"; }
rid_lock() { printf '%s/packages.linux-x64.lock.json' "${1%/*}"; }

verify_config() {
    is_regular "$STRICT_CONFIG" || fail 'strict NuGet configuration missing or redirected'
    [[ $(hash_file "$STRICT_CONFIG") == "$CONFIG_HASH" ]] ||
        fail 'strict NuGet configuration changed, including its credential or source policy'
}

load_manifests() {
    local row project keys rid lock digest key item
    local -a key_list
    local -A required=() count=([admin]=0 [client]=0 [auth]=0 [gateway]=0)
    local rid_count=0
    is_regular "$PROJECT_MANIFEST" && is_regular "$LOCK_MANIFEST" || fail 'manifest missing or redirected'
    while IFS= read -r row; do
        IFS=$'\t' read -r project keys rid <<< "$row"
        [[ $row == "$project"$'\t'"$keys"$'\t'"$rid" ]] || fail 'invalid project manifest row'
        [[ $project =~ ^[a-zA-Z0-9_.-]+(/[a-zA-Z0-9_.-]+)+[.]csproj$ &&
            /$project/ != *'/../'* && /$project/ != *'/./'* ]] || fail 'invalid project path'
        [[ -z ${PROJECT_KEYS[$project]+set} ]] || fail 'duplicate project in manifest'
        [[ $rid == '-' || $rid == 'linux-x64' ]] || fail 'invalid project RID'
        if [[ $keys != '-' ]]; then
            [[ $keys =~ ^(admin|client|auth|gateway)(,(admin|client|auth|gateway))*$ ]] ||
                fail 'invalid Docker closure key'
            IFS=, read -ra key_list <<< "$keys"
            for key in "${key_list[@]}"; do
                item="$project,$key"
                [[ -z ${required[$item]+set} ]] || fail 'duplicate Docker membership'
                required[$item]=1
                count[$key]=$(( count[$key] + 1 ))
            done
        fi
        PROJECTS+=("$project")
        PROJECT_KEYS[$project]=$keys
        PROJECT_RIDS[$project]=$rid
        lock=$(portable_lock "$project")
        [[ -z ${required[$lock]+set} ]] || fail 'multiple projects share a portable lock path'
        required[$lock]=1
        ALL_LOCKS+=("$lock")
        if [[ $rid == linux-x64 ]]; then
            rid_count=$((rid_count + 1))
            lock=$(rid_lock "$project")
            required[$lock]=1
            ALL_LOCKS+=("$lock")
        fi
    done < "$PROJECT_MANIFEST"
    [[ ${#PROJECTS[@]} == 36 && ${#ALL_LOCKS[@]} == 42 && $rid_count == 6 &&
        ${count[admin]} == 6 && ${count[client]} == 6 && ${count[auth]} == 6 &&
        ${count[gateway]} == 1 ]] || fail 'unexpected project or lock graph counts'
    for key in "${!DOCKER_ROOT[@]}"; do
        [[ ${required[${DOCKER_ROOT[$key]},$key]+set} ]] || fail 'Docker root missing from its closure'
    done
    [[ ${PROJECT_RIDS[$HELPER]-} == linux-x64 ]] || fail 'owner-helper RID root missing'
    while IFS= read -r row; do
        [[ $row =~ ^([a-f0-9]{64})\ \ ([a-zA-Z0-9_./-]+)$ ]] || fail 'invalid SHA-256 manifest row'
        digest=${BASH_REMATCH[1]}
        lock=${BASH_REMATCH[2]}
        [[ ${required[$lock]+set} && $lock == */packages*.lock.json && -z ${LOCK_HASH[$lock]+set} ]] ||
            fail 'unexpected or duplicate lock hash'
        LOCK_HASH[$lock]=$digest
    done < "$LOCK_MANIFEST"
    [[ ${#LOCK_HASH[@]} == 42 ]] || fail 'incomplete SHA-256 lock manifest'
    for lock in "${ALL_LOCKS[@]}"; do
        [[ ${LOCK_HASH[$lock]+set} ]] || fail "missing lock hash for $lock"
    done
}

check_lock_hashes() {
    local lock
    for lock in "$@"; do
        is_regular "$lock" || fail "missing or redirected lock: $lock"
        [[ $(hash_file "$lock") == "${LOCK_HASH[$lock]}" ]] || fail "lock hash mismatch: $lock"
    done
}

check_lock_inventory() {
    local current expected
    current=$(find . -path './.git' -prune -o -name 'packages*.lock.json' -print | sed 's#^\./##' | sort)
    expected=$(printf '%s\n' "$@" | sort)
    [[ $current == "$expected" ]] || fail 'on-disk lock inventory differs from the manifest'
    check_lock_hashes "$@"
}

check_clean() {
    local changes
    git diff --exit-code --quiet && git diff --cached --exit-code --quiet ||
        fail 'tracked source or index has changed'
    changes=$(git status --porcelain=v1 --untracked-files=all --ignore-submodules=none)
    [[ -z $changes ]] || fail 'checkout has untracked or modified source'
}

verify_identity() {
    local head=$1 overlay
    require_sha "$head"
    [[ $(git rev-parse --show-prefix) == '' ]] || fail 'run the contract at the repository root'
    [[ $(git rev-parse HEAD) == "$head" && ${EXPECTED_HEAD:-$head} == "$head" ]] ||
        fail 'actual and expected checkout SHA differ'
    [[ $(git rev-parse "$ACCEPTED_SOURCE^") == "$ACCEPTED_BASE" ]] || fail 'accepted source parent mismatch'
    overlay=$(git rev-list --first-parent --reverse "$ACCEPTED_SOURCE..$head" | sed -n '1p')
    [[ -n $overlay && $(git rev-parse "$overlay^") == "$ACCEPTED_SOURCE" &&
        $(git rev-parse "$overlay^{tree}") == "$ACCEPTED_OVERLAY" ]] ||
        fail 'accepted source and test overlay are absent from the first-parent history'
    if [[ ${GITHUB_ACTIONS:-} == true ]] && git symbolic-ref --quiet HEAD >/dev/null; then
        fail 'hosted proof requires a detached head checkout'
    fi
    check_clean
}

verify_all() {
    local head=$1 tracked expected preserved lock prior
    verify_identity "$head"
    verify_config
    load_manifests
    python3 scripts/ci/strict-restore-graph.py
    tracked=$(git ls-files | awk -F / '$NF ~ /^packages.*[.]lock[.]json$/' | sort)
    expected=$(printf '%s\n' "${ALL_LOCKS[@]}" | sort)
    [[ $tracked == "$expected" ]] || fail 'tracked lock inventory differs from the manifest'
    check_lock_inventory "${ALL_LOCKS[@]}"
    preserved=$(git ls-tree -r --name-only "$ACCEPTED_SOURCE" | awk -F / '$NF == "packages.lock.json"')
    [[ $(wc -l <<< "$preserved") == 12 ]] || fail 'accepted 12-lock inventory differs'
    while IFS= read -r lock; do
        prior=$(git show "$ACCEPTED_SOURCE:$lock" | sha256sum | cut -c1-64)
        [[ ${LOCK_HASH[$lock]-} == "$prior" ]] || fail "accepted lock changed: $lock"
    done <<< "$preserved"
}

check_credentials() {
    local name
    [[ ${PACKAGE_USERNAME:-} =~ ^[a-zA-Z0-9_.-]+$ ]] || fail 'missing or malformed approved package credential'
    # GitHub Actions may supply a stateless installation token with a dot-separated JWT.
    [[ ${PACKAGE_READ_TOKEN:-} =~ ^[a-zA-Z0-9_-]+$ ||
        ${PACKAGE_READ_TOKEN:-} =~ ^ghs_[0-9]+_[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+$ ]] ||
        fail 'missing or malformed approved package credential'
    while IFS= read -r name; do
        case "${name,,}" in
            nugetpackagesourcecredentials_*|nuget_packages|nuget_http_cache_path|nuget_fallback_packages|\
            nuget_plugin_paths|nuget_plugins_cache_path|nuget_credentialproviders_path|\
            vss_nuget_external_feed_endpoints|artifacts_credentialprovider_feed_endpoints|\
            restoresources|restoreadditionalprojectsources|restorefallbackfolders|\
            restoreadditionalprojectfallbackfolders|restorepackagespath|restorelockedmode|\
            restoreforceevaluate|nugetlockfilepath)
                fail 'ambient NuGet source, cache, credential provider or restore override'
                ;;
        esac
    done < <(compgen -e)
}

verify_sdk() { [[ $(dotnet --version) == "$SDK_VERSION" ]] || fail "expected SDK $SDK_VERSION"; }

require_empty_cache() {
    [[ -d $1 && ! -L $1 && -z $(find "$1" -mindepth 1 -print -quit) ]] ||
        fail 'proof cache is absent, redirected or nonempty'
}

json_quote() {
    local value=$1
    value=${value//\\/\\\\}
    value=${value//\"/\\\"}
    value=${value//$'\n'/\\n}
    value=${value//$'\r'/\\r}
    value=${value//$'\t'/\\t}
    [[ $value != *[[:cntrl:]]* ]] || fail 'control character in receipt metadata'
    printf '"%s"' "$value"
}

json_array() {
    local item separator=
    printf '['
    for item in "$@"; do
        printf '%s' "$separator"
        json_quote "$item"
        separator=,
    done
    printf ']'
}

json_field() { printf ',"%s":' "$1"; json_quote "$2"; }

lock_receipt() {
    local lock
    for lock in "${SELECTED_LOCKS[@]}"; do
        printf '%s  %s\n' "$(hash_file "$lock")" "$lock"
    done | sort
}

verify_assets() {
    local project path folder configs sources fallback expected_sources
    expected_sources=$(printf '%s\n' "$GITHUB_FEED" "$PUBLIC_FEED" | sort)
    for project in "${SELECTED_PROJECTS[@]}"; do
        path="${project%/*}/obj/project.assets.json"
        is_regular "$path" || fail "restore did not produce assets for $project"
        folder=$(sed -nE '/^[[:space:]]*"packageFolders": \{/,/^[[:space:]]*\},?$/s/^[[:space:]]*"([^"]+)": \{\},?$/\1/p' "$path")
        configs=$(sed -nE '/^[[:space:]]*"configFilePaths": \[/,/^[[:space:]]*\],?$/s/^[[:space:]]*"([^"]+)",?$/\1/p' "$path")
        sources=$(sed -nE '/^[[:space:]]*"sources": \{/,/^[[:space:]]*\},?$/s/^[[:space:]]*"([^"]+)": \{\},?$/\1/p' "$path" | sort)
        fallback=$(sed -nE '/^[[:space:]]*"fallbackFolders": \[/,/^[[:space:]]*\],?$/s/^[[:space:]]*"([^"]+)",?$/\1/p' "$path")
        [[ ${folder%/} == "$CACHE_PACKAGES" && $configs == "$CONFIG_PATH" ]] ||
            fail "effective cache or configuration differs for $project"
        [[ $sources == "$expected_sources" && -z $fallback ]] ||
            fail "effective sources or fallback folders differ for $project"
    done
}

package_receipt() {
    local file relative package version source digest expected
    local count=0
    while IFS= read -r -d '' file; do
        relative=${file#"$CACHE_PACKAGES"/}
        IFS=/ read -r package version _ <<< "$relative"
        [[ $package =~ ^[a-zA-Z0-9_.-]+$ && $version =~ ^[a-zA-Z0-9_.+-]+$ ]] ||
            fail 'unexpected package cache entry'
        source=$(sed -nE 's/^[[:space:]]*"source": "([^"]+)",?$/\1/p' "$file")
        digest=$(sed -nE 's/^[[:space:]]*"contentHash": "([A-Za-z0-9+/=]+)",?$/\1/p' "$file")
        expected=$PUBLIC_FEED
        [[ ${package,,} != zeka.* ]] || expected=$GITHUB_FEED
        [[ $source == "$expected" && ${#digest} == 88 ]] || fail "package source or content hash invalid for $package"
        printf '%s\t%s\t%s\t%s\n' "$package" "$version" "$source" "$digest"
        count=$((count + 1))
    done < <(find "$CACHE_PACKAGES" -name .nupkg.metadata -type f -print0 | sort -z)
    (( count > 0 )) || fail 'fresh restore produced no package metadata'
}

run_proof() {
    local proof=$1 root=$2 head=$3 receipt=$4
    local started scratch before after package_data native_codes native_exit=0 tree= os runner arch source_bound=true
    local -a command extras=()
    verify_sdk
    check_credentials
    [[ ! -e $receipt && ! -L $receipt ]] || fail 'receipt path already exists'
    scratch=$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/strict-restore.XXXXXX")
    CACHE_PACKAGES="$scratch/packages"
    local http_cache="$scratch/http"
    mkdir "$CACHE_PACKAGES" "$http_cache"
    require_empty_cache "$CACHE_PACKAGES"
    require_empty_cache "$http_cache"
    CONFIG_PATH="$(pwd -P)/$STRICT_CONFIG"
    before=$(lock_receipt)
    case $proof in
        owner-helper-linux-x64)
            extras=(--runtime linux-x64 -p:SelfContained=false -p:RestoreContractRid=linux-x64 "-p:ZekaSourceSha=$head")
            ;;
        docker-*) extras=(-p:UseAppHost=false) ;;
    esac
    command=(dotnet restore "$root" --locked-mode --no-cache --configfile "$CONFIG_PATH"
        -p:Configuration=Release -p:RestorePackagesWithLockFile=true -p:RestoreFallbackFolders=
        -p:RestoreAdditionalProjectSources= -p:RestoreAdditionalProjectFallbackFolders=
        "${extras[@]}")
    started=$(date -u +%FT%TZ)
    if (
        export NUGET_PACKAGES="$CACHE_PACKAGES" NUGET_HTTP_CACHE_PATH="$http_cache"
        export NuGetPackageSourceCredentials_github="Username=$PACKAGE_USERNAME;Password=$PACKAGE_READ_TOKEN;ValidAuthenticationTypes=Basic"
        unset PACKAGE_USERNAME PACKAGE_READ_TOKEN
        "${command[@]}"
    ) 2>&1 | { grep -Eo 'NU[0-9]{4}|NETSDK[0-9]{4}|MSB[0-9]{4}' || true; } > "$scratch/native-codes"; then
        native_exit=0
    else
        native_exit=${PIPESTATUS[0]}
    fi
    native_codes=$(sort -u "$scratch/native-codes")
    check_lock_hashes "${SELECTED_LOCKS[@]}"
    after=$(lock_receipt)
    [[ $before == "$after" ]] || fail 'lock hashes changed during restore'
    if (( native_exit == 0 )); then
        verify_assets
        package_data=$(package_receipt)
    else
        package_data=
    fi
    if [[ $proof == docker-* ]]; then
        tree=${STRICT_RESTORE_TREE:-}
        [[ $head != local ]] || source_bound=false
    else
        tree=$(git rev-parse HEAD^{tree})
    fi
    os=$(uname -s)
    arch=$(uname -m)
    runner=${RUNNER_NAME:-}
    {
        printf '{"schema":1'
        json_field proof "$proof"
        json_field head "$head"
        json_field tree "$tree"
        json_field base "${EXPECTED_BASE:-}"
        json_field event "${EVENT_SHA:-}"
        json_field syntheticMerge "${SYNTHETIC_MERGE_SHA:-}"
        printf ',"sourceBound":%s,"applicable":true,"isDockerProof":%s' "$source_bound" "$([[ $proof == docker-* ]] && printf true || printf false)"
        json_field root "$root"
        json_field sdk "$SDK_VERSION"
        json_field tfm net8.0
        json_field rid "$([[ $proof == owner-helper-linux-x64 ]] && printf linux-x64 || printf portable)"
        json_field utc "$started"
        json_field os "$os"
        json_field architecture "$arch"
        json_field runner "$runner"
        json_field runnerImage "${ImageOS:-}/${ImageVersion:-}"
        json_field run "${GITHUB_SERVER_URL:-}/${GITHUB_REPOSITORY:-}/actions/runs/${GITHUB_RUN_ID:-}"
        json_field job "${GITHUB_JOB:-}"
        json_field packages "$CACHE_PACKAGES"
        json_field httpCache "$http_cache"
        printf ',"initialPackagesEmpty":true,"initialHttpCacheEmpty":true'
        json_field nugetConfigSha256 "$CONFIG_HASH"
        json_field githubSource "$GITHUB_FEED"
        json_field githubPattern 'Zeka.*'
        json_field publicSource "$PUBLIC_FEED"
        json_field publicPattern '*'
        json_field locksBefore "$before"
        json_field locksAfter "$after"
        json_field packageSources "$package_data"
        json_field diagnosticCodes "$native_codes"
        printf ',"argv":'
        json_array "${command[@]}"
        printf ',"nativeExit":%d}\n' "$native_exit"
    } > "$receipt"
    if (( native_exit != 0 )); then
        printf 'strict-restore: %s failed with native exit %d (%s)\n' "$proof" "$native_exit" "$native_codes" >&2
        return "$native_exit"
    fi
}

run_linux() {
    local head=$1 directory=$2 project
    verify_all "$head"
    mkdir -p -- "$directory"
    SELECTED_PROJECTS=("${PROJECTS[@]}")
    SELECTED_LOCKS=("${ALL_LOCKS[@]}")
    run_proof portable zeka-web-api.sln "$head" "$directory/portable.json"
    check_clean
    check_lock_inventory "${ALL_LOCKS[@]}"
    SELECTED_PROJECTS=()
    for project in "${PROJECTS[@]}"; do
        [[ ${PROJECT_RIDS[$project]} != linux-x64 ]] || SELECTED_PROJECTS+=("$project")
    done
    run_proof owner-helper-linux-x64 "$HELPER" "$head" "$directory/owner-helper-linux-x64.json"
    check_clean
    check_lock_inventory "${ALL_LOCKS[@]}"
}

run_docker() {
    local key=$1 root=$2 head=$3 receipt=$4 project current expected
    [[ $key =~ ^(admin|client|auth|gateway)$ && ${DOCKER_ROOT[$key]} == "$root" ]] ||
        fail 'unknown Docker proof root or mismatched project'
    [[ $head == local ]] || require_sha "$head"
    verify_config
    load_manifests
    for project in "${PROJECTS[@]}"; do
        if [[ ,${PROJECT_KEYS[$project]}, == *",$key,"* ]]; then
            is_regular "$project" || fail "missing or redirected Docker project: $project"
            SELECTED_PROJECTS+=("$project")
            SELECTED_LOCKS+=("$(portable_lock "$project")")
        fi
    done
    current=$(find . -iname '*.csproj' -print | sed 's#^\./##' | sort)
    expected=$(printf '%s\n' "${SELECTED_PROJECTS[@]}" | sort)
    [[ $current == "$expected" ]] || fail 'Docker context contains an unexpected project subset'
    check_lock_inventory "${SELECTED_LOCKS[@]}"
    run_proof "docker-$key" "$root" "$head" "$receipt"
    check_lock_inventory "${SELECTED_LOCKS[@]}"
}

relevant() {
    local base=$1 head=$2 names path
    require_sha "$base"
    require_sha "$head"
    git cat-file -e "$base^{commit}" && git cat-file -e "$head^{commit}" || fail 'base or head commit missing'
    names=$(git diff --name-only --no-renames "$base" "$head" --) || fail 'cannot compare immutable base and head'
    while IFS= read -r path; do
        [[ $path != '"'* ]] || fail 'quoted changed path cannot be classified safely'
        case "$path" in
            Services/*|Shared/*|ApiGateways/*|Tools/*|scripts/*|.github/workflows/*|\
            *.sln|*.slnx|*.*proj|*.lock.json|*.config|*.props|*.targets|\
            global.json|*/global.json|Dockerfile*|*/Dockerfile*|*compose*.yaml|*compose*.yml|*.dockerignore)
                printf 'true\n'
                return
                ;;
        esac
    done <<< "$names"
    printf 'false\n'
}

gate() {
    [[ $1 == success && $2 == success && $3 == success ]] || fail 'source, detector or Linux prerequisite did not pass'
    if [[ $4 == true && $5 == success ]]; then
        printf 'PASS: Docker applicable\n'
    elif [[ $4 == false && $5 == skipped ]]; then
        printf 'PASS: Docker not applicable\n'
    else
        fail 'Docker applicability and result do not form an accepted combination'
    fi
}

context() {
    local head=$1 directory=$2 path git_digest copy_digest
    verify_all "$head"
    [[ ! -e $directory && ! -L $directory ]] || fail 'committed context destination exists'
    mkdir -- "$directory"
    git archive "$head" | tar -xf - -C "$directory"
    for path in "${PROJECTS[@]}" "${ALL_LOCKS[@]}" "$PROJECT_MANIFEST" "$LOCK_MANIFEST" "$STRICT_CONFIG" \
        global.json Directory.Build.props .dockerignore scripts/ci/strict-restore.sh scripts/ci/install-strict-sdk.sh; do
        is_regular "$directory/$path" || fail "committed context missing: $path"
        git_digest=$(git show "$head:$path" | sha256sum | cut -c1-64)
        copy_digest=$(hash_file "$directory/$path")
        [[ $git_digest == "$copy_digest" ]] || fail "committed context bytes differ: $path"
    done
}

main() {
    local action=${1:-}
    (( $# > 0 )) || fail 'missing contract command'
    shift
    case "$action:$#" in
        verify:1) verify_all "$@" ;;
        linux:2) run_linux "$@" ;;
        docker:4) run_docker "$@" ;;
        relevant:2) relevant "$@" ;;
        gate:5) gate "$@" ;;
        context:2) context "$@" ;;
        *) fail 'invalid contract command or arguments' ;;
    esac
}

[[ ${BASH_SOURCE[0]} != "$0" ]] || main "$@"

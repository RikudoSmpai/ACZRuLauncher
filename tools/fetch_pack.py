#!/usr/bin/env python3
"""Забирает пакет перевода по pack/manifest.json и сверяет sha256 и размер — то, что попадёт в exe релиза.

    python tools/fetch_pack.py pack/manifest.json dist/pack

Манифест: {"name": "acz-ru-pack.zip", "version": "...", "url": "https://... | путь", "asset_id": 123 | null,
           "sha256": "...", "size": 1}
asset_id — ассет черновика релиза этого репозитория (снаружи не виден): скачивание через API под GITHUB_TOKEN,
редирект на хранилище проходится без Authorization («Only one auth mechanism allowed»).
Иначе url: https-ссылка или локальный путь (сборка у себя). Ссылка-заглушка — понятная ошибка, а не 404.
Внутри zip проверяется manifest.json: format acz-ru-pack/1 и та же version.
"""
import hashlib, json, os, shutil, sys, urllib.error, urllib.request, zipfile

API = os.environ.get("GITHUB_API_URL", "https://api.github.com").rstrip("/")
REPO = os.environ.get("GITHUB_REPOSITORY", "RikudoSmpai/ACZRuLauncher")
TOKEN = os.environ.get("GITHUB_TOKEN", "")
UA = "ACZRuLauncher-release/1.0"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def download_plain(url, dest):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req) as r, open(dest, "wb") as f:
        shutil.copyfileobj(r, f)


def download_asset(asset_id, dest):
    if not TOKEN:
        sys.exit("нужен GITHUB_TOKEN: пакет лежит ассетом черновика релиза")
    url = f"{API}/repos/{REPO}/releases/assets/{asset_id}"
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/octet-stream",
                                               "Authorization": "Bearer " + TOKEN,
                                               "X-GitHub-Api-Version": "2022-11-28"})
    try:
        with urllib.request.build_opener(NoRedirect).open(req) as r, open(dest, "wb") as f:
            shutil.copyfileobj(r, f)
            return
    except urllib.error.HTTPError as e:
        if e.code not in (301, 302, 303, 307, 308):
            raise
        location = e.headers.get("Location")
    if not location:
        sys.exit(f"asset {asset_id}: редирект без Location")
    download_plain(location, dest)


for _s in (sys.stdout, sys.stderr):
    _s.reconfigure(encoding="utf-8", errors="replace")


def main():
    manifest, out = sys.argv[1], sys.argv[2]
    spec = json.load(open(manifest, encoding="utf-8"))
    os.makedirs(out, exist_ok=True)
    dest = os.path.join(out, spec.get("name") or "acz-ru-pack.zip")
    if spec.get("asset_id"):
        download_asset(spec["asset_id"], dest)
    else:
        src = spec.get("url") or ""
        if src.startswith("ЗАГЛУШКА") or not src:
            sys.exit("pack/manifest.json: ссылка на пакет — заглушка. Укажите url (или asset_id) и sha256/size пакета.")
        if src.startswith(("http://", "https://")):
            download_plain(src, dest)
        else:
            shutil.copyfile(src[7:] if src.startswith("file://") else src, dest)
    got = sha256(dest)
    if got != spec["sha256"].lower():
        sys.exit(f"{dest}: sha256 mismatch\n  expected {spec['sha256']}\n  got      {got}")
    if spec.get("size") and os.path.getsize(dest) != spec["size"]:
        sys.exit(f"{dest}: size {os.path.getsize(dest)} != {spec['size']}")
    m = json.loads(zipfile.ZipFile(dest).read("manifest.json"))
    if m.get("format") != "acz-ru-pack/1":
        sys.exit(f"{dest}: format {m.get('format')!r}, ждали acz-ru-pack/1")
    if spec.get("version") and m.get("version") != spec["version"]:
        sys.exit(f"{dest}: version {m.get('version')} != {spec['version']} из pack/manifest.json")
    print(f"{dest}: {os.path.getsize(dest)} bytes, sha256 ok, pack {m['version']}")


if __name__ == "__main__":
    main()

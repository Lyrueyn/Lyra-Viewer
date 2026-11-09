"""Publish the approved PDF extension release. Credentials stay in process memory."""
import argparse
import hashlib
import json
import os
import pathlib
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request

REPOSITORY = 'Lyrueyn/Lyra-Extention-PDF_Reader'
API = 'https://api.github.com/repos/' + REPOSITORY
TAG = 'v0.3.0'
ROOT = pathlib.Path(__file__).resolve().parent
ARCHIVE = ROOT / 'lyra-ext-pdf.zip'
EXPECTED_HASH = '7d5e5304ff6365671bc740137080fc4a50891dd3f98ffebc9ba55aa2abac3f7e'


def credential():
    env = dict(os.environ, GIT_TERMINAL_PROMPT='0', GCM_INTERACTIVE='Never')
    result = subprocess.run(['git', 'credential', 'fill'], input='protocol=https\nhost=github.com\n\n',
                            text=True, capture_output=True, env=env, check=False)
    if result.returncode:
        raise RuntimeError('Git credential lookup failed; no credentials were printed.')
    fields = dict(line.split('=', 1) for line in result.stdout.splitlines() if '=' in line)
    if not fields.get('password'):
        raise RuntimeError('No GitHub credential is available.')
    return fields['password']


def request(url, token=None, method='GET', data=None, content_type=None, binary=False):
    headers = {'User-Agent': 'Lyra-PDF-Release/0.3.0', 'Accept': 'application/vnd.github+json',
               'X-GitHub-Api-Version': '2022-11-28'}
    if token:
        if urllib.parse.urlsplit(url).hostname not in ('api.github.com', 'uploads.github.com'):
            raise RuntimeError('Refusing to send credentials to a non-API host.')
        headers['Authorization'] = 'Bearer ' + token
    if isinstance(data, dict):
        data = json.dumps(data, ensure_ascii=False).encode('utf-8')
        content_type = 'application/json'
    if content_type:
        headers['Content-Type'] = content_type
    if method == 'PATCH':
        # Use system curl for the final update; this host's Python TLS connection resets on PATCH.
        payload = ROOT / 'release-publish-payload.json'
        if payload.exists():
            if payload.read_bytes() != data:
                raise RuntimeError('Existing publication payload differs; refusing to overwrite.')
        else:
            payload.write_bytes(data)
        auth_config = 'header = "Authorization: Bearer ' + token + '"\n'
        result = subprocess.run(['curl.exe', '--http1.1', '--silent', '--show-error', '--fail-with-body',
                                 '--connect-timeout', '20', '--max-time', '90', '--request', 'PATCH',
                                 '--url', url, '--config', '-', '--header', 'Content-Type: application/json',
                                 '--header', 'User-Agent: Lyra-PDF-Release/0.3.0',
                                 '--header', 'X-GitHub-Api-Version: 2022-11-28',
                                 '--data-binary', '@' + str(payload)], input=auth_config.encode('utf-8'), capture_output=True)
        if result.returncode:
            message = result.stderr.decode('utf-8', errors='replace').replace(token, '[redacted]')
            raise RuntimeError('Release update transport failed: ' + message)
        return json.loads(result.stdout)
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=90) as response:
        body = response.read()
        return body if binary else json.loads(body)


def find_release(token):
    try:
        return request(API + '/releases/tags/' + TAG, token)
    except urllib.error.HTTPError as error:
        if error.code == 404:
            # A draft may be absent from the tag endpoint; inspect authenticated drafts too.
            releases = request(API + '/releases?per_page=100', token)
            matches = [release for release in releases if release['tag_name'] == TAG]
            if len(matches) > 1:
                raise RuntimeError('Multiple releases use this tag; refusing to create another.')
            return matches[0] if matches else None
        raise


def preflight(token):
    repo = request(API, token)
    if repo['full_name'] != REPOSITORY or not repo.get('permissions', {}).get('push'):
        raise RuntimeError('Repository identity or write permission check failed.')
    print('Repository write access verified:', repo['full_name'])
    existing = find_release(token)
    print('Version state:', 'not published' if not existing else 'draft' if existing['draft'] else 'published')


def verify_download(release):
    if release['tag_name'] != TAG or release['draft'] or release['prerelease']:
        raise RuntimeError('The release is not the expected stable version.')
    assets = [a for a in release['assets'] if a['name'] == ARCHIVE.name]
    if len(assets) != 1:
        raise RuntimeError('Expected exactly one matching installation asset.')
    asset = assets[0]
    if asset['state'] != 'uploaded' or asset['size'] != ARCHIVE.stat().st_size:
        raise RuntimeError('Published asset size or state mismatch.')
    if asset.get('digest') and asset['digest'] != 'sha256:' + EXPECTED_HASH:
        raise RuntimeError('Published asset digest mismatch.')
    # Public download: no credentials are attached to GitHub/CDN redirects.
    downloaded = request(asset['browser_download_url'], binary=True)
    if hashlib.sha256(downloaded).hexdigest() != EXPECTED_HASH:
        raise RuntimeError('Downloaded archive fingerprint mismatch.')
    output = ROOT / 'github-lyra-ext-pdf.zip'
    if output.exists():
        if output.read_bytes() != downloaded:
            raise RuntimeError('Existing verification download differs; refusing to overwrite.')
    else:
        output.write_bytes(downloaded)
    print(json.dumps({'release': release['html_url'], 'tag': release['tag_name'],
                      'asset': asset['browser_download_url'], 'bytes': asset['size'],
                      'sha256': EXPECTED_HASH, 'download_verified': True}, ensure_ascii=False))


def publish(token, commit):
    if hashlib.sha256(ARCHIVE.read_bytes()).hexdigest() != EXPECTED_HASH:
        raise RuntimeError('Local release archive changed after verification.')
    tag_ref = request(API + '/git/ref/tags/' + TAG, token)
    obj = tag_ref['object']
    if obj['type'] == 'tag':
        obj = request(API + '/git/tags/' + obj['sha'], token)['object']
    if obj['type'] != 'commit' or obj['sha'] != commit:
        raise RuntimeError('Remote version tag does not point to the expected commit.')
    notes = (ROOT / 'RELEASE_NOTES.md').read_text(encoding='utf-8')
    release = find_release(token)
    if release is None:
        release = request(API + '/releases', token, 'POST', {
            'tag_name': TAG, 'target_commitish': commit, 'name': 'PDF 阅读器 0.3.0',
            'body': notes, 'draft': True, 'prerelease': False})
        print('Created release draft:', release['id'], flush=True)
    existing = next((a for a in release['assets'] if a['name'] == ARCHIVE.name), None)
    if existing:
        if existing.get('digest') != 'sha256:' + EXPECTED_HASH:
            raise RuntimeError('Existing release asset differs; refusing to replace it.')
    else:
        upload = release['upload_url'].split('{')[0] + '?' + urllib.parse.urlencode({'name': ARCHIVE.name})
        asset = request(upload, token, 'POST', ARCHIVE.read_bytes(), 'application/zip')
        if asset['state'] != 'uploaded' or asset['size'] != ARCHIVE.stat().st_size:
            raise RuntimeError('Upload verification failed; release remains a draft.')
        if asset.get('digest') != 'sha256:' + EXPECTED_HASH:
            raise RuntimeError('Upload digest mismatch; release remains a draft.')
        print('Uploaded and verified:', asset['name'], asset['size'], flush=True)
    if release['draft']:
        release = request(release['url'], token, 'PATCH', {
            'name': 'PDF 阅读器 0.3.0', 'body': notes, 'draft': False, 'prerelease': False, 'make_latest': 'true'})
        print('Published:', release['html_url'], flush=True)
    latest = request(API + '/releases/latest', token)
    verify_download(latest)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['preflight', 'publish', 'verify'])
    parser.add_argument('--commit')
    args = parser.parse_args()
    try:
        if args.action == 'verify':
            verify_download(request(API + '/releases/latest'))
        else:
            token = credential()
            if args.action == 'preflight':
                preflight(token)
            else:
                if not args.commit:
                    raise RuntimeError('An expected commit is required.')
                publish(token, args.commit)
    except urllib.error.HTTPError as error:
        print('GitHub request failed: HTTP', error.code, error.reason, file=sys.stderr)
        sys.exit(1)
    except Exception as error:
        print(type(error).__name__ + ': ' + str(error), file=sys.stderr)
        sys.exit(1)

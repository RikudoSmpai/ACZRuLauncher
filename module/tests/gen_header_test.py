"""Negative checks of module/tools/gen_header.py: each broken table must be refused."""
import copy, json, os, sys
sys.dont_write_bytecode = True   # no __pycache__ in module/tools
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'tools'))
import gen_header as gh
base = json.load(open(gh.SRC, encoding='utf-8'))
tmp = os.path.join(gh.ROOT, 'build', 'gen_header_test.json')
os.makedirs(os.path.dirname(tmp), exist_ok=True)


def case(name, fn):
    d = copy.deepcopy(base)
    fn(d)
    json.dump(d, open(tmp, 'w', encoding='utf-8'), ensure_ascii=False)
    gh.SRC = tmp
    try:
        gh.build()
        print('NOT REFUSED', name); return False
    except SystemExit as e:
        print('refused', name, '-', e); return True


def p(d, rid):
    return [x for x in d['patches'] if x['id'] == rid][0]


def longer(d):          # L=5 string, 6 letters (> L+1) like the old КОРАБЛИ
    x = p(d, 'result.2ac504'); x['bytes'] = (b'ABCDEFG').hex()


def longer_same_len(d):  # L+2 letters squeezed into L+1 bytes field is impossible; bytes of len L+2
    x = p(d, 'result.2ac504'); x['bytes'] = (b'ABCDEF' + b'\0').hex()


def nul_inside(d):
    x = p(d, 'result.2ac504'); x['bytes'] = (b'AB\0CD\0').hex()


def per_string_hash(d):
    p(d, 'result.2ac504')['sha256'] = '00' * 32


def group_hash(d):       # a hash of the two short label slots alone could be brute-forced
    d['groups']['labels']['sha256'] = '00' * 32


def no_table_hash(d):
    del d['table_sha256']


def short_table_hash(d):
    d['table_sha256'] = d['table_sha256'][:62]


def old_format(d):
    d['format'] = 'acz-exe-strings/2'


ok = all([case('text longer than L+1', longer), case('bytes of L+2', longer_same_len), case('NUL inside', nul_inside),
          case('per-string sha256', per_string_hash), case('sha256 per group', group_hash),
          case('no table sha256', no_table_hash), case('table sha256 of 31 bytes', short_table_hash),
          case('format /2', old_format)])
gh.SRC = os.path.join(gh.ROOT, 'patches.json')
gh.build()
print('original table builds')
os.remove(tmp)
print('RESULT', 'OK' if ok else 'FAILED')
sys.exit(0 if ok else 1)

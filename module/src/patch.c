#include "patch.h"
#include "sha256.h"
#include <stdio.h>
#include <string.h>

static uint16_t rd16(const uint8_t *p) { return (uint16_t)(p[0] | p[1] << 8); }
static uint32_t rd32(const uint8_t *p) { return (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24; }

#define WHY(...) do { if (why && whylen) snprintf(why, whylen, __VA_ARGS__); } while (0)

int acz_check_header(const uint8_t *base, size_t avail, const acz_expect *ex, char *why, size_t whylen)
{
    uint32_t lfanew, nsec, optsz, so, i;
    int found = 0;
    if (avail < 0x40 || base[0] != 'M' || base[1] != 'Z') { WHY("no MZ header"); return 0; }
    lfanew = rd32(base + 0x3c);
    if (lfanew > 0x800 || lfanew + 24 + 0xf0 > avail || memcmp(base + lfanew, "PE\0\0", 4) != 0) { WHY("no PE header"); return 0; }
    if (rd16(base + lfanew + 4) != 0x8664 || rd16(base + lfanew + 24) != 0x20b) { WHY("not an x64 image"); return 0; }
    nsec = rd16(base + lfanew + 6);
    optsz = rd16(base + lfanew + 20);
    if (rd32(base + lfanew + 8) != ex->timestamp) { WHY("PE timestamp %08x, expected %08x (other exe build)", rd32(base + lfanew + 8), ex->timestamp); return 0; }
    if (rd32(base + lfanew + 24 + 56) != ex->size_of_image) { WHY("SizeOfImage %x, expected %x", rd32(base + lfanew + 24 + 56), ex->size_of_image); return 0; }
    so = lfanew + 24 + optsz;
    if (nsec > 96 || so + nsec * 40 > avail) { WHY("bad section table"); return 0; }
    for (i = 0; i < nsec; i++) {
        const uint8_t *s = base + so + 40 * i;
        if (memcmp(s, ".rdata\0\0", 8) == 0) {
            found = 1;
            if (rd32(s + 12) != ex->rdata_rva || rd32(s + 8) != ex->rdata_vsize) {
                WHY(".rdata at %x size %x, expected %x size %x", rd32(s + 12), rd32(s + 8), ex->rdata_rva, ex->rdata_vsize);
                return 0;
            }
        }
    }
    if (!found) { WHY("no .rdata section"); return 0; }
    return 1;
}

void acz_table_hash(const uint8_t *base, const acz_patch *p, int n, uint8_t out[32])
{
    acz_sha256_ctx c;
    int i;
    acz_sha256_init(&c);
    for (i = 0; i < n; i++) {
        uint8_t hd[6];
        hd[0] = (uint8_t)p[i].rva; hd[1] = (uint8_t)(p[i].rva >> 8); hd[2] = (uint8_t)(p[i].rva >> 16); hd[3] = (uint8_t)(p[i].rva >> 24);
        hd[4] = (uint8_t)p[i].slot; hd[5] = (uint8_t)(p[i].slot >> 8);
        acz_sha256_update(&c, hd, 6);
        acz_sha256_update(&c, base + p[i].rva, p[i].slot);
    }
    acz_sha256_final(&c, out);
}

int acz_verify_all(const uint8_t *base, const acz_expect *ex, const acz_patch *p, int n, int ng,
                   const uint8_t table_sha256[32], const int *group_enabled, char *why, size_t whylen)
{
    int i, j, k, ours = 0, theirs = 0;
    uint8_t h[32];
    for (i = 0; i < n; i++) {
        const uint8_t *nb = p[i].new_bytes;
        int len = p[i].len, end = len + 1;          /* the object [0, L+1) */
        if (p[i].len >= p[i].slot || p[i].rva < ex->rdata_rva || (uint64_t)p[i].rva + p[i].slot > (uint64_t)ex->rdata_rva + ex->rdata_vsize) {
            WHY("patch %d (rva %x) outside .rdata", i, p[i].rva);
            return 0;
        }
        if (p[i].group >= ng) { WHY("patch %d: unknown group", i); return 0; }
        for (j = 0; j < i; j++)
            if (p[i].rva < p[j].rva + p[j].slot && p[j].rva < p[i].rva + p[i].slot) { WHY("patch %d overlaps patch %d", i, j); return 0; }
        /* new text: no NUL inside it, zero padding after it, within [0, L+1) */
        for (k = 0; k < end && nb[k]; k++) ;
        for (j = k; j < end; j++) if (nb[j]) { WHY("patch %d: bytes after the NUL", i); return 0; }
        /* a text of L+1 bytes ends at the first byte after the object: it must lie in the slot (zero, see below) */
        if (k == end && end >= p[i].slot) { WHY("patch %d: text of L+1 bytes without a zero after the object", i); return 0; }
    }
    acz_table_hash(base, p, n, h);
    if (memcmp(h, table_sha256, 32) != 0) {
        /* every enabled string already holds its new text (module loaded twice?): reported separately */
        for (i = 0; i < n; i++)
            if (group_enabled[p[i].group]) {
                if (memcmp(base + p[i].rva, p[i].new_bytes, (size_t)p[i].len + 1) == 0) ours++; else theirs++;
            }
        if (ours && !theirs) WHY("table: already applied");
        else WHY("table: sha256 mismatch (other exe build)");
        return 0;
    }
    /* the hash says the slots are the original ones; the zero after an L+1 text is checked once more anyway */
    for (i = 0; i < n; i++)
        if (p[i].new_bytes[p[i].len] != 0 && base[p[i].rva + p[i].len + 1] != 0) { WHY("patch %d: no zero after the object", i); return 0; }
    return 1;
}

#define MAXP 256

int acz_apply(uint8_t *base, const acz_patch *p, int n, const int *group_enabled, const acz_mem_ops *ops,
              int *applied, char *why, size_t whylen)
{
    static uint8_t saved[MAXP][64];
    size_t w;
    int done[MAXP];
    int i, k = 0, ok = 1;
    *applied = 0;
    if (n > MAXP) { WHY("too many patches"); return 0; }
    for (i = 0; i < n; i++) {
        if ((size_t)p[i].len + 1 > sizeof saved[0]) { WHY("patch %d: string too long", i); return 0; }
        memcpy(saved[i], base + p[i].rva, (size_t)p[i].len + 1);
        done[i] = 0;
    }
    for (i = 0; i < n && ok; i++) {
        uint32_t old = 0;
        if (!group_enabled[p[i].group]) continue;
        w = (size_t)p[i].len + 1;                   /* only the object [0, L+1) */
        if (!ops->unprotect(ops->ctx, base + p[i].rva, w, &old)) { WHY("patch %d: cannot make the page writable", i); ok = 0; break; }
        memcpy(base + p[i].rva, p[i].new_bytes, w);
        done[i] = 1;
        if (!ops->protect(ops->ctx, base + p[i].rva, w, old)) { WHY("patch %d: cannot restore page protection", i); ok = 0; break; }
        if (memcmp(base + p[i].rva, p[i].new_bytes, w) != 0) { WHY("patch %d: read back differs", i); ok = 0; break; }
        k++;
    }
    if (!ok) {
        /* roll back everything written so far */
        for (i = 0; i < n; i++) {
            uint32_t old = 0;
            if (!done[i]) continue;
            w = (size_t)p[i].len + 1;
            if (ops->unprotect(ops->ctx, base + p[i].rva, w, &old)) {
                memcpy(base + p[i].rva, saved[i], w);
                ops->protect(ops->ctx, base + p[i].rva, w, old);
            }
        }
        return 0;
    }
    *applied = k;
    return 1;
}

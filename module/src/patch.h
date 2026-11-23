/* Core of the module: identify acz.exe by its PE header and ONE sha256 over all string slots of the table in
 * .rdata, then overwrite the strings of the enabled groups (only the bytes of each original string with its NUL). No Windows calls here: memory protection comes from the caller, so the same
 * code runs in the game (module.c) and in the offline test on an image laid out from the file (tests/). */
#pragma once
#include <stddef.h>
#include <stdint.h>

/* 1 = probe build: every group of patches.json is applied, also those disabled for release */
#ifndef ACZ_PROBE_BUILD
#define ACZ_PROBE_BUILD 0
#endif

/* Write rule: the original string of length L is the object [rva, rva+L+1) (with its NUL). Only these L+1
 * bytes are ever written. The new text is at most L+1 bytes; a text of exactly L+1 bytes ends at the zero
 * right after the object, which is zero in the original (it is in the slot, so the table hash covers it) and
 * is never written. The rest of the slot may be other objects (an empty string, a zero constant): not touched.
 *
 * Identification: ONE sha256 over all slots of the table (every group, enabled or not) in table order,
 *   for each patch: rva (u32 LE) || slot (u16 LE) || the `slot` original bytes.
 * No hash per string or per group: the hash of one or a few short slots could be brute-forced back to the
 * English text; the hash of the whole table cannot. */
typedef struct acz_patch {
    uint32_t rva;              /* start of the original string, inside .rdata */
    uint16_t len;              /* L: length of the original string (without NUL) */
    uint16_t slot;             /* string + its zero tail up to the next data: hashed (table), never written beyond L+1 */
    uint8_t group;             /* index in acz_group_names / acz_group_enabled */
    const uint8_t *new_bytes;  /* exactly len+1 bytes: new text + zero padding (the bytes written) */
} acz_patch;

typedef struct acz_mem_ops {
    /* make [p, p+n) writable, remember the old protection in *old; 0 = failure */
    int (*unprotect)(void *ctx, uint8_t *p, size_t n, uint32_t *old);
    /* restore the old protection; 0 = failure */
    int (*protect)(void *ctx, uint8_t *p, size_t n, uint32_t old);
    void *ctx;
} acz_mem_ops;

typedef struct acz_expect {
    uint32_t timestamp, size_of_image, rdata_rva, rdata_vsize;
} acz_expect;

/* 1 = PE header matches (x64 image, TimeDateStamp, SizeOfImage, .rdata at the expected RVA/size).
 * `avail` = readable bytes from base (headers only are read). */
int acz_check_header(const uint8_t *base, size_t avail, const acz_expect *ex, char *why, size_t whylen);

/* 1 = every slot (enabled or not) lies inside .rdata, does not overlap others, belongs to one of the `ng`
 * groups, every new text fits [0, L+1) (an L+1 text needs a zero after the object inside the slot), and the
 * sha256 of the whole table matches `table_sha256`. All or nothing: one mismatch anywhere -> 0. Reads only the
 * slot bytes. `group_enabled` only tells "already applied" from "other exe build" in `why`. */
int acz_verify_all(const uint8_t *base, const acz_expect *ex, const acz_patch *p, int n, int ng,
                   const uint8_t table_sha256[32], const int *group_enabled, char *why, size_t whylen);

/* sha256 of the table over the slots at `base` (as described above) */
void acz_table_hash(const uint8_t *base, const acz_patch *p, int n, uint8_t out[32]);

/* Write the enabled patches: bytes [rva, rva+len+1) only. Call only after acz_verify_all. Every object is copied first; if any step fails,
 * the slots already written are restored and 0 is returned. Returns 1 and *applied on success. */
int acz_apply(uint8_t *base, const acz_patch *p, int n, const int *group_enabled, const acz_mem_ops *ops,
              int *applied, char *why, size_t whylen);

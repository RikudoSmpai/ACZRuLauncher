/* SHA-256 (FIPS 180-4), own small implementation: no extra DLL (bcrypt) in the module. */
#pragma once
#include <stddef.h>
#include <stdint.h>

typedef struct acz_sha256_ctx {
    uint32_t h[8];
    uint64_t total;   /* bytes hashed so far */
    size_t fill;      /* bytes in buf */
    uint8_t buf[64];
} acz_sha256_ctx;

void acz_sha256_init(acz_sha256_ctx *c);
void acz_sha256_update(acz_sha256_ctx *c, const uint8_t *data, size_t n);
void acz_sha256_final(acz_sha256_ctx *c, uint8_t out[32]);
/* one call: init + update + final */
void acz_sha256(const uint8_t *data, size_t n, uint8_t out[32]);

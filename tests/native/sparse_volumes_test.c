#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include "libfsapfs_container_superblock.h"
#include "libfsapfs_checksum.h"

static void checksum(uint8_t *data) {
    uint64_t value = 0;
    libfsapfs_checksum_calculate_fletcher64(&value, data + 8, 4088, 0, NULL);
    memcpy(data, &value, 8);
}
int main(int argc, char **argv) {
    uint8_t data[4096];
    libfsapfs_container_superblock_t *block = NULL;
    FILE *file = argc == 2 ? fopen(argv[1], "rb") : NULL;
    if (!file || fread(data, 1, sizeof(data), file) != sizeof(data)) return 1;
    fclose(file);
    if (libfsapfs_container_superblock_initialize(&block, NULL) != 1) return 2;
    if (libfsapfs_container_superblock_read_data(block, data, sizeof(data), NULL) != 1 ||
        block->number_of_volumes != 1 || block->volume_object_identifiers[0] != 1026) return 3;
    memset(data + 184, 0, 800);
    uint64_t oid = 1111; memcpy(data + 184, &oid, 8);
    oid = 2222; memcpy(data + 184 + 17 * 8, &oid, 8);
    oid = 3333; memcpy(data + 184 + 99 * 8, &oid, 8);
    checksum(data);
    if (libfsapfs_container_superblock_read_data(block, data, sizeof(data), NULL) != 1 ||
        block->number_of_volumes != 3 || block->volume_object_identifiers[0] != 1111 ||
        block->volume_object_identifiers[1] != 2222 || block->volume_object_identifiers[2] != 3333 ||
        block->volume_object_identifiers[3] != 0) return 4;
    memset(data + 184, 0, 800); checksum(data);
    if (libfsapfs_container_superblock_read_data(block, data, sizeof(data), NULL) != 1 ||
        block->number_of_volumes != 0 || block->volume_object_identifiers[0] != 0) return 5;
    libfsapfs_container_superblock_free(&block, NULL);
    puts("PASS: synthetic sparse slot 2, multiple sparse slots including 99, and reused empty superblock.");
    return 0;
}

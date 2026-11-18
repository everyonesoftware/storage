#include <stdio.h>
#include <stdlib.h>

/* Service-private ABI probe. Public clients must never pass these arguments. */
extern char *storage_execute(const char *root, const char *actor, const char *command);
extern void storage_free(char *response);

int main(int argc, char **argv) {
    if (argc != 4) {
        fprintf(stderr, "usage: abi-driver root trusted-actor-json command-json\n");
        return 2;
    }
    char *response = storage_execute(argv[1], argv[2], argv[3]);
    if (response == NULL) {
        fprintf(stderr, "core returned null instead of an envelope\n");
        return 3;
    }
    puts(response);
    storage_free(response);
    /* A null free must be harmless; callers can always clean up on error. */
    storage_free(NULL);
    return 0;
}

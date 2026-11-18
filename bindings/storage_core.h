#ifndef STORAGE_CORE_H
#define STORAGE_CORE_H
#ifdef __cplusplus
extern "C" {
#endif
/* Service-internal ABI. Inputs are UTF-8 NUL-terminated strings borrowed for
 * the duration of this synchronous call. Caller authenticates the actor and
 * constructs repository root and sourcePath; never forward public client JSON.
 * Copy the JSON envelope before calling storage_free exactly once. Null or
 * invalid UTF-8 inputs produce InvalidArgument. Invalid non-null pointers are
 * caller bugs and cannot be validated by the ABI. */
char *storage_execute(const char *repository_root_utf8,
                      const char *trusted_actor_json_utf8,
                      const char *command_json_utf8);
/* NULL is harmless. Only free unmodified pointers returned by this library. */
void storage_free(char *response);
#ifdef __cplusplus
}
#endif
#endif

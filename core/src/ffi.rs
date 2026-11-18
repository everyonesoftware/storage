use super::{Error, Result};
use std::ffi::{c_char, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
fn response_error(code: &str, message: &str) -> String {
    serde_json::json!({"ok":false,"error":{"code":code,"message":message}}).to_string()
}
unsafe fn input<'a>(pointer: *const c_char) -> Result<&'a str> {
    if pointer.is_null() {
        return Err(Error::new("InvalidArgument", "null input"));
    }
    CStr::from_ptr(pointer)
        .to_str()
        .map_err(|_| Error::new("InvalidArgument", "input must be UTF-8"))
}
/// Executes a service-internal command. The returned allocation belongs to this library.
/// # Safety
/// Every non-null input must point to a readable NUL-terminated byte string for
/// the call's duration. Copy the response before freeing it exactly once.
#[no_mangle]
pub unsafe extern "C" fn storage_execute(
    root: *const c_char,
    actor: *const c_char,
    command: *const c_char,
) -> *mut c_char {
    let response = catch_unwind(AssertUnwindSafe(|| {
        match (input(root), input(actor), input(command)) {
            (Ok(root), Ok(actor), Ok(command)) => super::execute(root, actor, command).to_string(),
            _ => response_error("InvalidArgument", "null or invalid UTF-8 input"),
        }
    }))
    .unwrap_or_else(|_| response_error("InternalError", "core panicked"));
    // JSON serialization escapes embedded NUL; the fallback is likewise NUL-free.
    CString::new(response).unwrap_or_else(|_|CString::new("{\"ok\":false,\"error\":{\"code\":\"InternalError\",\"message\":\"encoding error\"}}").expect("literal has no NUL")).into_raw()
}
/// Releases a response allocated by storage_execute. NULL is harmless.
/// # Safety
/// Non-null pointers must be unmodified live allocations from storage_execute,
/// passed exactly once, and must not be read after this call.
#[no_mangle]
pub unsafe extern "C" fn storage_free(response: *mut c_char) {
    if !response.is_null() {
        drop(CString::from_raw(response));
    }
}

#[cfg(target_os = "android")]
mod android {
    use super::*;
    use jni::{
        objects::{JClass, JString},
        sys::jstring,
        JNIEnv,
    };
    #[no_mangle]
    pub extern "system" fn Java_com_everyonesoftware_storage_service_NativeCore_execute(
        mut env: JNIEnv,
        _class: JClass,
        root: JString,
        actor: JString,
        command: JString,
    ) -> jstring {
        let response = catch_unwind(AssertUnwindSafe(|| {
            let parse = |env: &mut JNIEnv, s: &JString| -> Result<String> {
                env.get_string(s)
                    .map(Into::into)
                    .map_err(|_| Error::new("InvalidArgument", "invalid Java string"))
            };
            match (
                parse(&mut env, &root),
                parse(&mut env, &actor),
                parse(&mut env, &command),
            ) {
                (Ok(root), Ok(actor), Ok(command)) => {
                    super::super::execute(&root, &actor, &command).to_string()
                }
                _ => response_error("InvalidArgument", "invalid Java input"),
            }
        }))
        .unwrap_or_else(|_| response_error("InternalError", "core panicked"));
        match env.new_string(response) {
            Ok(s) => s.into_raw(),
            Err(_) => std::ptr::null_mut(),
        }
    }
}

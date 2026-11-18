use super::{Error, Result};
use sha2::{Digest, Sha256};
use std::{
    fs::{self, File, OpenOptions},
    io::{Read, Write},
    path::{Path, PathBuf},
};
fn digest(path: &Path) -> Result<(String, u64)> {
    let mut input = File::open(path)?;
    if !input.metadata()?.is_file() {
        return Err(Error::new(
            "InvalidArgument",
            "source must be a regular file",
        ));
    }
    let mut hash = Sha256::new();
    let mut total = 0u64;
    let mut buffer = [0u8; 65536];
    loop {
        let n = input.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        hash.update(&buffer[..n]);
        total += n as u64;
    }
    Ok((format!("{:x}", hash.finalize()), total))
}
pub(super) fn verify(path: &Path, sha: &str, len: u64) -> Result<()> {
    let (actual, bytes) = digest(path)
        .map_err(|e| Error::new("IntegrityError", format!("blob unavailable: {}", e.message)))?;
    if actual != sha || bytes != len {
        return Err(Error::new("IntegrityError", "blob hash or length mismatch"));
    }
    Ok(())
}
#[cfg(unix)]
fn sync_dir(path: &Path) -> Result<()> {
    File::open(path)?.sync_all()?;
    Ok(())
}
#[cfg(not(unix))]
fn sync_dir(_path: &Path) -> Result<()> {
    Ok(())
}
#[cfg(not(windows))]
fn publish(from: &Path, to: &Path) -> Result<()> {
    fs::rename(from, to)?;
    Ok(())
}
#[cfg(windows)]
fn publish(from: &Path, to: &Path) -> Result<()> {
    use std::os::windows::ffi::OsStrExt;
    #[link(name = "kernel32")]
    extern "system" {
        fn MoveFileExW(from: *const u16, to: *const u16, flags: u32) -> i32;
    }
    let a: Vec<u16> = from.as_os_str().encode_wide().chain(Some(0)).collect();
    let b: Vec<u16> = to.as_os_str().encode_wide().chain(Some(0)).collect();
    // WRITE_THROUGH waits for publication before committing metadata.
    if unsafe { MoveFileExW(a.as_ptr(), b.as_ptr(), 8) } == 0 {
        return Err(std::io::Error::last_os_error().into());
    }
    Ok(())
}
struct Stage(PathBuf);
impl Drop for Stage {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}
pub(super) fn ingest(root: &Path, source: &Path) -> Result<(String, u64)> {
    let mut input = File::open(source)?;
    if !input.metadata()?.is_file() {
        return Err(Error::new(
            "InvalidArgument",
            "source must be a regular file",
        ));
    }
    let stage = Stage(root.join("staging").join(super::id()));
    let mut output = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&stage.0)?;
    let mut hash = Sha256::new();
    let mut total = 0u64;
    let mut buffer = [0u8; 65536];
    loop {
        let n = input.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        output.write_all(&buffer[..n])?;
        hash.update(&buffer[..n]);
        total += n as u64;
    }
    output.sync_all()?;
    drop(output);
    #[cfg(test)]
    super::recovery::crash_point("staged");
    let sha = format!("{:x}", hash.finalize());
    let destination = root.join("blobs").join(&sha);
    if destination.try_exists()? {
        verify(&destination, &sha, total)?;
    } else {
        publish(&stage.0, &destination)?;
        sync_dir(&root.join("blobs"))?;
    }
    #[cfg(test)]
    super::recovery::crash_point("published");
    Ok((sha, total))
}

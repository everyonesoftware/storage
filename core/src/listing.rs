use super::*;

// Includes the complete success envelope, leaving headroom below Android's 512 KiB
// response bound (and Windows' 1 MiB frame). Count UTF-8 JSON bytes, not characters.
const MAX_RESPONSE_BYTES: usize = 400 * 1024;
const MAX_RECORDS: usize = 500;

pub(super) fn bounded_list(
    field: &str,
    records: impl IntoIterator<Item = Result<Value>>,
) -> Result<Value> {
    let encoded_len = |value: &Value| {
        serde_json::to_vec(value)
            .map(|bytes| bytes.len())
            .map_err(|e| Error::new("InternalError", e.to_string()))
    };
    // false is one byte longer than true, so this is conservative when truncated.
    let mut bytes = encoded_len(&json!({"ok":true,"result":{field:[],"hasMore":false}}))?;
    let mut entries = Vec::new();
    let mut has_more = false;
    for record in records {
        if entries.len() == MAX_RECORDS {
            has_more = true;
            break;
        }
        let record = record?;
        let added = encoded_len(&record)? + usize::from(!entries.is_empty());
        if added > MAX_RESPONSE_BYTES - bytes {
            has_more = true;
            break;
        }
        bytes += added;
        entries.push(record);
    }
    Ok(json!({field:entries,"hasMore":has_more}))
}

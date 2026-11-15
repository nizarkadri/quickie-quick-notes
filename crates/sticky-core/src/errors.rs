//! Core error type. The numeric codes are the FFI status field (blueprint §6/§7).

#[derive(Debug, thiserror::Error)]
pub enum CoreError {
    /// status = 1
    #[error("not found: {0}")]
    NotFound(String),
    /// status = 2
    #[error("validation: {0}")]
    Validation(String),
    /// status = 3
    #[error("sqlite error: {0}")]
    Sqlite(#[from] rusqlite::Error),
    /// status = 4
    #[error("unsupported schema")]
    UnsupportedSchema,
    /// status = 5
    #[error("internal panic: {0}")]
    Internal(String),
}

impl CoreError {
    /// FFI `FfiResult.status`: 0 = OK, 1 = NotFound, 2 = Validation,
    /// 3 = Sqlite, 4 = UnsupportedSchema, 5 = Internal/panic.
    pub fn code(&self) -> i32 {
        match self {
            CoreError::NotFound(_) => 1,
            CoreError::Validation(_) => 2,
            CoreError::Sqlite(_) => 3,
            CoreError::UnsupportedSchema => 4,
            CoreError::Internal(_) => 5,
        }
    }
}

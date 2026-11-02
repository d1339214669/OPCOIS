-- ============================================================
-- task-001 DICOM Storage SCP 核心存储层 —— PostgreSQL 索引建表 DDL
-- 库：dicom
-- 幂等：重复执行不报错（CREATE TABLE/INDEX IF NOT EXISTS）。
-- 表结构与 spec 文档 §8 一致。后续加字段用 ALTER TABLE ADD COLUMN IF NOT EXISTS。
-- ============================================================

CREATE TABLE IF NOT EXISTS dicom_instance (
    internal_id          VARCHAR(64)  PRIMARY KEY,      -- 内部唯一 ID，即文件名
    sop_instance_uid     VARCHAR(64)  NOT NULL UNIQUE,  -- 业务 UID，DICOM 全局唯一
    sop_class_uid        VARCHAR(64),                    -- 如 1.2.840.10008.5.1.4.1.1.2
    patient_id           VARCHAR(64),
    patient_name         VARCHAR(255),
    study_instance_uid   VARCHAR(64),                    -- 非唯一（一 study 多 series）
    series_instance_uid  VARCHAR(64),
    modality             VARCHAR(16),
    study_date           VARCHAR(8),                     -- DICOM YYYYMMDD 原样
    study_time           VARCHAR(14),                    -- 可选，HHMMSS.FFFFFF 原样
    study_description    VARCHAR(1024),
    series_number        VARCHAR(16),
    instance_number      VARCHAR(16),
    file_path            TEXT         NOT NULL,          -- 相对存储目录，如 <ab>/<id>
    file_size_bytes      BIGINT,
    received_at          TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_file_path UNIQUE (file_path)
);

-- 辅助索引（可选，供后续 C-FIND 用；便于核对）
CREATE INDEX IF NOT EXISTS idx_dicom_patient_id     ON dicom_instance (patient_id);
CREATE INDEX IF NOT EXISTS idx_dicom_study_uid      ON dicom_instance (study_instance_uid);
CREATE INDEX IF NOT EXISTS idx_dicom_series_uid     ON dicom_instance (series_instance_uid);
CREATE INDEX IF NOT EXISTS idx_dicom_modality_date  ON dicom_instance (modality, study_date);
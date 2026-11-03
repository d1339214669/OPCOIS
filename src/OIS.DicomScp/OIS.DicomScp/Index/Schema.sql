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

-- ============================================================
-- task-002 出站 SCU 转发 —— 转发记录表 forward_log
-- 追加日志表：每次 C-STORE 转发成功/失败各写一行（AC-05），失败也必须可见。
-- 幂等：CREATE TABLE / INDEX IF NOT EXISTS，重复执行不报错。
-- 与 dicom_instance 为外键弱关联（不强制 FK）；转发失败（如文件被删）时 internal_id 仍记录引用，detail 写明原因。
-- ============================================================

-- task-002：C-STORE 转发记录（每次成功/失败各一行，AC-05）
CREATE TABLE IF NOT EXISTS forward_log (
    id                 BIGSERIAL     PRIMARY KEY,
    remote_scp_name    VARCHAR(128)  NOT NULL,          -- 目标别名（RemoteScps[].Name）
    internal_id        VARCHAR(64)   NOT NULL,          -- 转发实例的内部 ID（文件名）
    sop_instance_uid   VARCHAR(64),                     -- 转发的实例业务 UID
    sop_class_uid      VARCHAR(64),
    study_instance_uid VARCHAR(64),
    series_instance_uid VARCHAR(64),
    status             VARCHAR(16)   NOT NULL,          -- 'success' | 'failed'
    detail             TEXT,                            -- 成功: 状态码/空; 失败: 可读原因
    forwarded_at       TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- 加速: 按目标+实例查转发历史、按时间倒序
CREATE INDEX IF NOT EXISTS idx_fwd_target_instance ON forward_log (remote_scp_name, sop_instance_uid);
CREATE INDEX IF NOT EXISTS idx_fwd_time            ON forward_log (forwarded_at DESC);
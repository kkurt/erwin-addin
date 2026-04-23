-- ============================================================================
-- MSSQL 2022 - v2 (Erwin DM uyumlu - daha defensive)
-- v1 uzerine kullanici degisiklikleri uygulanmis hali
-- ============================================================================
-- ERWIN UYUM KURALLARI (v1 basariyla parse edildi, ayni kurallar):
-- 1. CONSTRAINT <n> ... syntax'i YOK
-- 2. PK: tablo sonunda isimsiz "PRIMARY KEY (col)"
-- 3. UNIQUE/CHECK: tablo disinda ayri "ALTER TABLE ... ADD" ile
-- 4. FK: tablo disinda ayri "ALTER TABLE ... ADD FOREIGN KEY"
-- 5. DEFAULT: kolon-inline
-- 6. DATETIME2(0) yerine DATETIME2; VARCHAR(MAX) yerine NVARCHAR(MAX)
-- ----------------------------------------------------------------------------
-- MANUEL AYARLANMASI GEREKEN CASE'LER:
-- - IDX-07 (NCL->CL PK swap): ORDERS PK Index Editor'dan
-- - IDX-08 (filtered WHERE): IX_CUSTOMER_EMAIL/IX_CUSTOMER_TAXNO Index Editor
-- - COL-12 (Turkish_CI_AS): full_name Column Editor
-- - COL-14 (computed line_total): ORDER_ITEM Column Editor
-- - COL-15, TBL-05 (comments): Comment alanlari
-- - PART-01, PART-02 (partition): Table Editor Partitions tab
-- - STO-02 (PAGE compression): TRANSACTION_LOG Storage
-- - VW-05 (indexed/SCHEMABINDING): VW_ORDER_DETAIL Materialized property
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Schemas (ops yeni schema - TBL-04)
-- ----------------------------------------------------------------------------
CREATE SCHEMA app AUTHORIZATION dbo;
GO
CREATE SCHEMA sales AUTHORIZATION dbo;
GO
CREATE SCHEMA ops AUTHORIZATION dbo;
GO

-- ----------------------------------------------------------------------------
-- Sequences
-- ----------------------------------------------------------------------------
CREATE SEQUENCE app.SEQ_ORDER_NUMBER
    AS BIGINT
    START WITH 1
    INCREMENT BY 10
    MINVALUE 1
    NO MAXVALUE
    NO CACHE;
GO

CREATE SEQUENCE app.SEQ_TRANSACTION_LOG
    AS BIGINT
    START WITH 1
    INCREMENT BY 1
    MINVALUE 1
    NO MAXVALUE
    CACHE 100;
GO

-- ----------------------------------------------------------------------------
-- PRODUCT (baseline - degismez)
-- ----------------------------------------------------------------------------
CREATE TABLE app.PRODUCT (
    product_id     INT             NOT NULL,
    product_code   VARCHAR(30)     NOT NULL,
    product_name   VARCHAR(200)    NOT NULL,
    list_price     DECIMAL(12,2)   NOT NULL,
    is_active      BIT             NOT NULL,
    created_at     DATETIME2       NOT NULL,
    PRIMARY KEY (product_id)
);
GO

CREATE NONCLUSTERED INDEX IX_PRODUCT_NAME ON app.PRODUCT (product_name);
GO

-- ----------------------------------------------------------------------------
-- PROMOTION (composite PK - PART-02 partition icin hazirlik)
-- ----------------------------------------------------------------------------
CREATE TABLE app.PROMOTION (
    promotion_id    INT             NOT NULL,
    code            VARCHAR(30)     NOT NULL,
    description     VARCHAR(500)    NULL,
    discount_pct    DECIMAL(5,2)    NOT NULL,
    start_date      DATE            NOT NULL,
    end_date        DATE            NOT NULL,
    PRIMARY KEY (promotion_id, start_date)
);
GO

-- ----------------------------------------------------------------------------
-- CAMPAIGN (TBL-01: yeni v2)
-- ----------------------------------------------------------------------------
CREATE TABLE app.CAMPAIGN (
    campaign_id           INT             NOT NULL,
    promotion_id          INT             NOT NULL,
    promotion_start_date  DATE            NOT NULL,
    campaign_name         VARCHAR(200)    NOT NULL,
    channel               VARCHAR(20)     NOT NULL,
    launch_date           DATE            NOT NULL,
    budget                DECIMAL(15,2)   NOT NULL,
    PRIMARY KEY (campaign_id, launch_date)
);
GO

-- ----------------------------------------------------------------------------
-- CUSTOMER
-- ----------------------------------------------------------------------------
CREATE TABLE app.CUSTOMER (
    customer_id      INT             NOT NULL,
    full_name        VARCHAR(150)    NOT NULL,
    email            VARCHAR(200)    NOT NULL,
    tax_no           VARCHAR(20)     NULL,
    address          VARCHAR(250)    NULL,
    mobile_no        VARCHAR(20)     NULL,
    email_verified   BIT             NOT NULL DEFAULT 0,
    status           VARCHAR(10)     NOT NULL DEFAULT 'ACTIVE',
    created_at       DATETIME2       NOT NULL,
    PRIMARY KEY (customer_id)
);
GO

CREATE UNIQUE NONCLUSTERED INDEX IX_CUSTOMER_TAXNO
    ON app.CUSTOMER (tax_no);
GO

CREATE NONCLUSTERED INDEX IX_CUSTOMER_EMAIL
    ON app.CUSTOMER (email)
    INCLUDE (full_name);
GO

CREATE TRIGGER app.TR_CUSTOMER_AUDIT
ON app.CUSTOMER
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(email) OR UPDATE(status)
    BEGIN
        PRINT 'Customer audit: critical field changed';
    END
END;
GO

-- ----------------------------------------------------------------------------
-- ORDERS
-- ----------------------------------------------------------------------------
CREATE TABLE app.ORDERS (
    order_id        INT             NOT NULL,
    customer_id     INT             NOT NULL,
    promotion_id    INT             NULL,
    order_date      DATETIME2       NOT NULL,
    order_amount    BIGINT          NOT NULL,
    order_status    VARCHAR(20)     NOT NULL DEFAULT 'PENDING',
    last_updated    DATETIME2       NULL,
    PRIMARY KEY (order_id)
);
GO

CREATE NONCLUSTERED INDEX IX_ORDERS_CUSTOMER
    ON app.ORDERS (customer_id, order_date);
GO

CREATE NONCLUSTERED INDEX IX_ORDERS_DATE_DESC
    ON app.ORDERS (order_date DESC);
GO

CREATE TRIGGER app.TR_ORDERS_TIMESTAMP
ON app.ORDERS
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE o
    SET last_updated = SYSUTCDATETIME()
    FROM app.ORDERS o
    INNER JOIN inserted i ON o.order_id = i.order_id;

    IF UPDATE(order_status)
    BEGIN
        INSERT INTO app.TRANSACTION_LOG (txn_id, customer_id, txn_date, txn_amount, txn_type)
        SELECT
            NEXT VALUE FOR app.SEQ_TRANSACTION_LOG,
            i.customer_id, SYSUTCDATETIME(), i.order_amount, 'STATUS_CHG'
        FROM inserted i;
    END
END;
GO

-- ----------------------------------------------------------------------------
-- ORDER_ITEM (TBL-04: ops schema)
-- ----------------------------------------------------------------------------
CREATE TABLE ops.ORDER_ITEM (
    order_id       INT             NOT NULL,
    line_no        INT             NOT NULL,
    product_id     INT             NOT NULL,
    quantity       INT             NOT NULL,
    unit_price     DECIMAL(12,2)   NOT NULL,
    discount_pct   DECIMAL(5,2)    NULL,
    PRIMARY KEY (order_id, line_no)
);
GO

CREATE NONCLUSTERED INDEX IX_ORDER_ITEM_PRODUCT
    ON ops.ORDER_ITEM (product_id);
GO

-- ----------------------------------------------------------------------------
-- TRANSACTION_LOG
-- ----------------------------------------------------------------------------
CREATE TABLE app.TRANSACTION_LOG (
    log_id         BIGINT          IDENTITY(1,1) NOT NULL,
    txn_id         BIGINT          NOT NULL,
    customer_id    INT             NULL,
    txn_date       DATETIME2       NOT NULL,
    txn_amount     DECIMAL(18,2)   NOT NULL,
    txn_type       VARCHAR(10)     NOT NULL,
    PRIMARY KEY (log_id, txn_date)
);
GO

CREATE NONCLUSTERED INDEX IX_TX_LOG
    ON app.TRANSACTION_LOG (customer_id, txn_date);
GO

-- ----------------------------------------------------------------------------
-- CUSTOMER_HISTORY (TBL-03: rename)
-- ----------------------------------------------------------------------------
CREATE TABLE app.CUSTOMER_HISTORY (
    customer_id    INT             NOT NULL,
    snapshot_date  DATE            NOT NULL,
    snapshot_data  NVARCHAR(MAX)   NULL,
    PRIMARY KEY (customer_id, snapshot_date)
);
GO

-- ----------------------------------------------------------------------------
-- UNIQUE CONSTRAINTS (ayri ALTER TABLE statement'lari)
-- ----------------------------------------------------------------------------
ALTER TABLE app.PRODUCT ADD UNIQUE (product_code);
GO

ALTER TABLE app.CUSTOMER ADD UNIQUE (email);
GO

-- ----------------------------------------------------------------------------
-- CHECK CONSTRAINTS (ayri ALTER TABLE statement'lari)
-- ----------------------------------------------------------------------------
ALTER TABLE app.PROMOTION ADD CHECK (end_date >= start_date);
GO

ALTER TABLE app.CAMPAIGN ADD CHECK (budget >= 0);
GO

ALTER TABLE app.ORDERS ADD CHECK (order_amount >= 0 AND order_amount <= 1000000);
GO

ALTER TABLE ops.ORDER_ITEM ADD CHECK (quantity > 0);
GO

-- ----------------------------------------------------------------------------
-- FOREIGN KEYS (ayri ALTER TABLE statement'lari)
-- ----------------------------------------------------------------------------
-- FK_CAMPAIGN_PROMOTION (TBL-01 ile yeni)
ALTER TABLE app.CAMPAIGN
    ADD FOREIGN KEY (promotion_id, promotion_start_date)
    REFERENCES app.PROMOTION (promotion_id, start_date)
    ON DELETE CASCADE;
GO

-- FK_ORDERS_CUSTOMER (baseline)
ALTER TABLE app.ORDERS
    ADD FOREIGN KEY (customer_id) REFERENCES app.CUSTOMER (customer_id);
GO

-- FK_ORDER_ITEM_ORDERS (FK-03: NO ACTION -> CASCADE)
ALTER TABLE ops.ORDER_ITEM
    ADD FOREIGN KEY (order_id) REFERENCES app.ORDERS (order_id)
    ON DELETE CASCADE;
GO

-- FK_ORDER_ITEM_PRODUCT (baseline)
ALTER TABLE ops.ORDER_ITEM
    ADD FOREIGN KEY (product_id) REFERENCES app.PRODUCT (product_id);
GO

-- FK_TX_LOG_CUSTOMER (FK-01: yeni)
ALTER TABLE app.TRANSACTION_LOG
    ADD FOREIGN KEY (customer_id) REFERENCES app.CUSTOMER (customer_id);
GO

-- ----------------------------------------------------------------------------
-- VIEWS
-- ----------------------------------------------------------------------------
CREATE VIEW app.VW_CUSTOMER_SUMMARY
AS
SELECT
    c.customer_id,
    c.full_name,
    c.email,
    c.email_verified
FROM app.CUSTOMER c;
GO

CREATE VIEW app.VW_ORDER_DETAIL
AS
SELECT
    o.order_id,
    o.customer_id,
    o.order_date,
    o.order_amount,
    o.order_status
FROM app.ORDERS o
WHERE o.order_status IN ('OPEN','PENDING');
GO

CREATE VIEW app.VW_TRANSACTION_DAILY
AS
SELECT
    CAST(txn_date AS DATE) AS txn_day,
    customer_id,
    txn_type,
    COUNT(*)               AS txn_count,
    SUM(txn_amount)        AS total_amount
FROM app.TRANSACTION_LOG
GROUP BY CAST(txn_date AS DATE), customer_id, txn_type;
GO
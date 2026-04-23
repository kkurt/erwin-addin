-- ============================================================================
-- MSSQL 2022 - v1 BASELINE (Erwin DM uyumlu)
-- Test modeli: CC (Complete Compare) icin baseline
-- ============================================================================
-- ERWIN UYUM KURALLARI:
-- 1. CONSTRAINT <name> PRIMARY KEY/FK/CHECK/DEFAULT/UNIQUE syntax'i Erwin
--    parser tarafindan kabul edilmiyor. Bu yuzden:
--    - PK: tablo sonunda isimsiz "PRIMARY KEY (col)" formunda
--    - DEFAULT: kolon-inline "col INT DEFAULT 0"
--    - CHECK: tablo sonunda isimsiz "CHECK (...)" formunda
--    - FK: ayri "ALTER TABLE ... ADD FOREIGN KEY ..." statement'lari ile
--    - UNIQUE: tablo sonunda isimsiz "UNIQUE (col)" formunda
-- 2. DATETIME2(0) yerine DATETIME2
-- 3. VARCHAR(MAX) yerine NVARCHAR(MAX)
-- 4. Constraint isimlerini kaybediyoruz (Erwin otomatik isim verir);
--    CC karsilastirmasi sirasinda bu fark bilincli olarak goz ardi edilmeli
-- 5. NO ACTION default davranis oldugu icin yazilmadi
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Schemas
-- ----------------------------------------------------------------------------
CREATE SCHEMA app AUTHORIZATION dbo;
GO
CREATE SCHEMA sales AUTHORIZATION dbo;
GO

-- ----------------------------------------------------------------------------
-- Sequences
-- ----------------------------------------------------------------------------
CREATE SEQUENCE app.SEQ_ORDER_NUMBER
    AS BIGINT
    START WITH 1
    INCREMENT BY 1
    MINVALUE 1
    NO MAXVALUE
    NO CACHE;
GO

-- ----------------------------------------------------------------------------
-- PRODUCT (master, baseline)
-- ----------------------------------------------------------------------------
CREATE TABLE app.PRODUCT (
    product_id     INT             NOT NULL,
    product_code   VARCHAR(30)     NOT NULL,
    product_name   VARCHAR(200)    NOT NULL,
    list_price     DECIMAL(12,2)   NOT NULL,
    is_active      BIT             NOT NULL,
    created_at     DATETIME2       NOT NULL,
    PRIMARY KEY (product_id),
    UNIQUE (product_code)
);
GO

CREATE NONCLUSTERED INDEX IX_PRODUCT_NAME ON app.PRODUCT (product_name);
GO

-- ----------------------------------------------------------------------------
-- PROMOTION
-- ----------------------------------------------------------------------------
CREATE TABLE app.PROMOTION (
    promotion_id    INT             NOT NULL,
    code            VARCHAR(30)     NOT NULL,
    description     VARCHAR(500)    NULL,
    discount_pct    DECIMAL(5,2)    NOT NULL,
    start_date      DATE            NOT NULL,
    end_date        DATE            NOT NULL,
    PRIMARY KEY (promotion_id),
    CHECK (end_date >= start_date)
);
GO

-- ----------------------------------------------------------------------------
-- CUSTOMER
-- ----------------------------------------------------------------------------
CREATE TABLE app.CUSTOMER (
    customer_id    INT             NOT NULL,
    full_name      VARCHAR(150)    NOT NULL,
    email          VARCHAR(200)    NOT NULL,
    tax_no         VARCHAR(20)     NULL,
    address        VARCHAR(100)    NULL,
    mobile_phone   VARCHAR(20)     NULL,
    fax_number     VARCHAR(20)     NULL,
    status         VARCHAR(10)     NOT NULL,
    created_at     DATETIME2       NOT NULL,
    PRIMARY KEY (customer_id),
    UNIQUE (tax_no)
);
GO

CREATE NONCLUSTERED INDEX IX_CUSTOMER_TAXNO ON app.CUSTOMER (tax_no);
GO

-- ----------------------------------------------------------------------------
-- ORDERS
-- FK: FK_ORDERS_CUSTOMER -> app.CUSTOMER
--     FK_ORDERS_PROMOTION -> app.PROMOTION (v2'de drop)
-- ----------------------------------------------------------------------------
CREATE TABLE app.ORDERS (
    order_id        INT             NOT NULL,
    customer_id     INT             NULL,
    promotion_id    INT             NULL,
    order_date      DATETIME2       NOT NULL,
    order_amount    INT             NOT NULL,
    order_status    VARCHAR(20)     NOT NULL DEFAULT 'NEW',
    last_updated    DATETIME2       NULL,
    PRIMARY KEY (order_id),
    CHECK (order_amount > 0)
);
GO

CREATE NONCLUSTERED INDEX IX_ORDERS_DATE
    ON app.ORDERS (order_date);
GO

CREATE NONCLUSTERED INDEX IX_ORDERS_CUSTOMER
    ON app.ORDERS (customer_id);
GO

CREATE NONCLUSTERED INDEX IX_ORDERS_PROMOTION
    ON app.ORDERS (promotion_id);
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
END;
GO

-- ----------------------------------------------------------------------------
-- ORDER_ITEM (v1 sales schema)
-- FK: FK_ORDER_ITEM_ORDERS  -> app.ORDERS
--     FK_ORDER_ITEM_PRODUCT -> app.PRODUCT
-- ----------------------------------------------------------------------------
CREATE TABLE sales.ORDER_ITEM (
    order_id       INT             NOT NULL,
    line_no        INT             NOT NULL,
    product_id     INT             NOT NULL,
    quantity       INT             NOT NULL DEFAULT 1,
    unit_price     DECIMAL(18,4)   NOT NULL,
    discount_pct   DECIMAL(5,2)    NOT NULL,
    PRIMARY KEY (order_id)
);
GO

CREATE NONCLUSTERED INDEX IX_OI_PROD
    ON sales.ORDER_ITEM (product_id);
GO

-- ----------------------------------------------------------------------------
-- TRANSACTION_LOG (v1 PK yok, IDENTITY yok, FK yok)
-- ----------------------------------------------------------------------------
CREATE TABLE app.TRANSACTION_LOG (
    txn_id         BIGINT          NOT NULL,
    customer_id    INT             NULL,
    txn_date       DATETIME2       NOT NULL,
    txn_amount     DECIMAL(18,2)   NOT NULL,
    txn_type       VARCHAR(10)     NOT NULL
);
GO

CREATE NONCLUSTERED INDEX IX_TX_LOG
    ON app.TRANSACTION_LOG (txn_date, customer_id);
GO

-- ----------------------------------------------------------------------------
-- PRODUCT_ARCHIVE (v2'de drop)
-- ----------------------------------------------------------------------------
CREATE TABLE app.PRODUCT_ARCHIVE (
    product_id     INT             NOT NULL,
    product_name   VARCHAR(200)    NOT NULL,
    archived_at    DATETIME2       NOT NULL,
    PRIMARY KEY (product_id)
);
GO

-- ----------------------------------------------------------------------------
-- CUSTOMER_BACKUP (v2'de CUSTOMER_HISTORY rename)
-- ----------------------------------------------------------------------------
CREATE TABLE app.CUSTOMER_BACKUP (
    customer_id    INT             NOT NULL,
    snapshot_date  DATE            NOT NULL,
    snapshot_data  NVARCHAR(MAX)   NULL,
    PRIMARY KEY (customer_id, snapshot_date)
);
GO

-- ----------------------------------------------------------------------------
-- FOREIGN KEYS (ayri ALTER TABLE statement'lari)
-- ----------------------------------------------------------------------------
ALTER TABLE app.ORDERS
    ADD FOREIGN KEY (customer_id) REFERENCES app.CUSTOMER (customer_id);
GO

ALTER TABLE app.ORDERS
    ADD FOREIGN KEY (promotion_id) REFERENCES app.PROMOTION (promotion_id);
GO

ALTER TABLE sales.ORDER_ITEM
    ADD FOREIGN KEY (order_id) REFERENCES app.ORDERS (order_id);
GO

ALTER TABLE sales.ORDER_ITEM
    ADD FOREIGN KEY (product_id) REFERENCES app.PRODUCT (product_id);
GO

-- ----------------------------------------------------------------------------
-- VIEWS
-- ----------------------------------------------------------------------------
CREATE VIEW app.VW_CUSTOMER_SUMMARY
AS
SELECT
    c.customer_id,
    c.full_name,
    c.email
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
WHERE o.order_status = 'OPEN';
GO

CREATE VIEW app.VW_LEGACY_CUSTOMER
AS
SELECT
    customer_id,
    full_name,
    fax_number
FROM app.CUSTOMER;
GO

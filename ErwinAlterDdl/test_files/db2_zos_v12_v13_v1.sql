-- ============================================================================
-- DB2 z/OS V12 & V13 - v1 BASELINE (Erwin DM uyumlu)
-- ============================================================================
-- ERWIN UYUM KURALLARI:
-- 1. CONSTRAINT <n> ... syntax'i YOK
-- 2. PK: tablo sonunda isimsiz, UNIQUE/CHECK/FK: ALTER TABLE ile
-- 3. WITH DEFAULT: kolon-inline
-- 4. Trigger'lar yorumlandi - Erwin DB2 z/OS parser BEFORE/AFTER INSERT OR
--    UPDATE syntax'ini parse edemiyor. Trigger'lari Erwin'de manuel ekle.
-- ----------------------------------------------------------------------------
-- MANUEL AYARLANMASI GEREKEN CASE'LER:
-- - TRG-02 (TR_ORDERS_TIMESTAMP): manuel (Trigger Editor)
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Sequences
-- ----------------------------------------------------------------------------
CREATE SEQUENCE APP.SEQ_ORDER_NUMBER
    AS BIGINT
    START WITH 1
    INCREMENT BY 1
    MINVALUE 1
    NO MAXVALUE
    NO CACHE
    NO CYCLE;

-- ----------------------------------------------------------------------------
-- Tablespaces
-- ----------------------------------------------------------------------------
CREATE TABLESPACE TS_PROD IN APPDB
    USING STOGROUP APPSG
    BUFFERPOOL BP1
    CCSID UNICODE
    MAXPARTITIONS 1
    DSSIZE 4G;

CREATE TABLESPACE TS_PROMO IN APPDB
    USING STOGROUP APPSG
    BUFFERPOOL BP1
    CCSID UNICODE
    MAXPARTITIONS 1
    DSSIZE 4G;

CREATE TABLESPACE TS_CUST IN APPDB
    USING STOGROUP APPSG
    BUFFERPOOL BP1
    CCSID UNICODE
    MAXPARTITIONS 1
    DSSIZE 4G;

CREATE TABLESPACE TS_ORD IN APPDB
    USING STOGROUP APPSG
    BUFFERPOOL BP1
    CCSID UNICODE
    MAXPARTITIONS 1
    DSSIZE 4G;

CREATE TABLESPACE TS_OIS IN APPDB
    USING STOGROUP APPSG
    BUFFERPOOL BP1
    CCSID UNICODE;

CREATE TABLESPACE TS_TXL IN APPDB
    USING STOGROUP APPSG
    BUFFERPOOL BP1
    CCSID UNICODE
    MAXPARTITIONS 1
    DSSIZE 4G;

CREATE TABLESPACE TS_PARC IN APPDB
    USING STOGROUP APPSG
    BUFFERPOOL BP1
    CCSID UNICODE;

CREATE TABLESPACE TS_CBKP IN APPDB
    USING STOGROUP APPSG
    BUFFERPOOL BP1
    CCSID UNICODE;

-- ----------------------------------------------------------------------------
-- PRODUCT
-- ----------------------------------------------------------------------------
CREATE TABLE APP.PRODUCT (
    product_id     INTEGER          NOT NULL,
    product_code   VARCHAR(30)      NOT NULL,
    product_name   VARCHAR(200)     NOT NULL,
    list_price     DECIMAL(12,2)    NOT NULL,
    is_active      SMALLINT         NOT NULL,
    created_at     TIMESTAMP        NOT NULL,
    PRIMARY KEY (product_id)
)
IN APPDB.TS_PROD;

CREATE UNIQUE INDEX APP.IX_PRODUCT_PK
    ON APP.PRODUCT (product_id)
    USING STOGROUP APPSG
    CLUSTER;

CREATE UNIQUE INDEX APP.UQI_PRODUCT_CODE
    ON APP.PRODUCT (product_code)
    USING STOGROUP APPSG;

CREATE INDEX APP.IX_PRODUCT_NAME
    ON APP.PRODUCT (product_name)
    USING STOGROUP APPSG;

-- ----------------------------------------------------------------------------
-- PROMOTION
-- ----------------------------------------------------------------------------
CREATE TABLE APP.PROMOTION (
    promotion_id    INTEGER         NOT NULL,
    code            VARCHAR(30)     NOT NULL,
    description     VARCHAR(500),
    discount_pct    DECIMAL(5,2)    NOT NULL,
    start_date      DATE            NOT NULL,
    end_date        DATE            NOT NULL,
    PRIMARY KEY (promotion_id)
)
IN APPDB.TS_PROMO;

CREATE UNIQUE INDEX APP.IX_PROMOTION_PK
    ON APP.PROMOTION (promotion_id)
    USING STOGROUP APPSG
    CLUSTER;

-- ----------------------------------------------------------------------------
-- CUSTOMER
-- ----------------------------------------------------------------------------
CREATE TABLE APP.CUSTOMER (
    customer_id    INTEGER          NOT NULL,
    full_name      VARCHAR(150)     NOT NULL,
    email          VARCHAR(200)     NOT NULL,
    tax_no         VARCHAR(20),
    address        VARCHAR(100),
    mobile_phone   VARCHAR(20),
    fax_number     VARCHAR(20),
    status         VARCHAR(10)      NOT NULL,
    created_at     TIMESTAMP        NOT NULL,
    PRIMARY KEY (customer_id)
)
IN APPDB.TS_CUST;

CREATE UNIQUE INDEX APP.IX_CUSTOMER_PK
    ON APP.CUSTOMER (customer_id)
    USING STOGROUP APPSG
    CLUSTER;

CREATE UNIQUE INDEX APP.UQI_CUSTOMER_TAXNO
    ON APP.CUSTOMER (tax_no)
    USING STOGROUP APPSG;

CREATE INDEX APP.IX_CUSTOMER_TAXNO
    ON APP.CUSTOMER (tax_no)
    USING STOGROUP APPSG;

-- ----------------------------------------------------------------------------
-- ORDERS
-- ----------------------------------------------------------------------------
CREATE TABLE APP.ORDERS (
    order_id       INTEGER          NOT NULL,
    customer_id    INTEGER,
    promotion_id   INTEGER,
    order_date     TIMESTAMP        NOT NULL,
    order_amount   INTEGER          NOT NULL,
    order_status   VARCHAR(20)      NOT NULL WITH DEFAULT 'NEW',
    last_updated   TIMESTAMP,
    PRIMARY KEY (order_id)
)
IN APPDB.TS_ORD;

CREATE UNIQUE INDEX APP.IX_ORDERS_PK ON APP.ORDERS (order_id)
    USING STOGROUP APPSG
    CLUSTER;

CREATE INDEX APP.IX_ORDERS_DATE       ON APP.ORDERS (order_date)
    USING STOGROUP APPSG;

CREATE INDEX APP.IX_ORDERS_CUSTOMER   ON APP.ORDERS (customer_id)
    USING STOGROUP APPSG;

CREATE INDEX APP.IX_ORDERS_PROMOTION  ON APP.ORDERS (promotion_id)
    USING STOGROUP APPSG;

-- TRG-02 (TR_ORDERS_TIMESTAMP) - Erwin parse edemiyor, manuel ekle
-- CREATE TRIGGER APP.TR_ORDERS_TIMESTAMP
-- NO CASCADE BEFORE INSERT OR UPDATE ON APP.ORDERS
-- REFERENCING NEW AS N
-- FOR EACH ROW
-- BEGIN ATOMIC
--     SET N.last_updated = CURRENT TIMESTAMP;
-- END;

-- ----------------------------------------------------------------------------
-- ORDER_ITEM (v1 SALES qualifier)
-- ----------------------------------------------------------------------------
CREATE TABLE SALES.ORDER_ITEM (
    order_id       INTEGER          NOT NULL,
    line_no        INTEGER          NOT NULL,
    product_id     INTEGER          NOT NULL,
    quantity       INTEGER          NOT NULL WITH DEFAULT 1,
    unit_price     DECIMAL(18,4)    NOT NULL,
    discount_pct   DECIMAL(5,2)     NOT NULL,
    PRIMARY KEY (order_id)
)
IN APPDB.TS_OIS;

CREATE UNIQUE INDEX SALES.IX_ORDER_ITEM_PK
    ON SALES.ORDER_ITEM (order_id)
    USING STOGROUP APPSG
    CLUSTER;

CREATE INDEX SALES.IX_OI_PROD
    ON SALES.ORDER_ITEM (product_id)
    USING STOGROUP APPSG;

-- ----------------------------------------------------------------------------
-- TRANSACTION_LOG
-- ----------------------------------------------------------------------------
CREATE TABLE APP.TRANSACTION_LOG (
    txn_id         BIGINT           NOT NULL,
    customer_id    INTEGER,
    txn_date       TIMESTAMP        NOT NULL,
    txn_amount     DECIMAL(18,2)    NOT NULL,
    txn_type       VARCHAR(10)      NOT NULL
)
IN APPDB.TS_TXL;

CREATE INDEX APP.IX_TX_LOG
    ON APP.TRANSACTION_LOG (txn_date, customer_id)
    USING STOGROUP APPSG;

-- ----------------------------------------------------------------------------
-- PRODUCT_ARCHIVE (v2'de drop)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.PRODUCT_ARCHIVE (
    product_id     INTEGER          NOT NULL,
    product_name   VARCHAR(200)     NOT NULL,
    archived_at    TIMESTAMP        NOT NULL,
    PRIMARY KEY (product_id)
)
IN APPDB.TS_PARC;

CREATE UNIQUE INDEX APP.IX_PRODUCT_ARCHIVE_PK
    ON APP.PRODUCT_ARCHIVE (product_id)
    USING STOGROUP APPSG
    CLUSTER;

-- ----------------------------------------------------------------------------
-- CUSTOMER_BACKUP (v2'de CUSTOMER_HISTORY rename)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.CUSTOMER_BACKUP (
    customer_id    INTEGER          NOT NULL,
    snapshot_date  DATE             NOT NULL,
    snapshot_data  CLOB(1M),
    PRIMARY KEY (customer_id, snapshot_date)
)
IN APPDB.TS_CBKP;

CREATE UNIQUE INDEX APP.IX_CUSTOMER_BACKUP_PK
    ON APP.CUSTOMER_BACKUP (customer_id, snapshot_date)
    USING STOGROUP APPSG
    CLUSTER;

-- ----------------------------------------------------------------------------
-- CHECK CONSTRAINTS
-- ----------------------------------------------------------------------------
ALTER TABLE APP.PROMOTION ADD CHECK (end_date >= start_date);
ALTER TABLE APP.ORDERS    ADD CHECK (order_amount > 0);

-- ----------------------------------------------------------------------------
-- FOREIGN KEYS
-- ----------------------------------------------------------------------------
ALTER TABLE APP.ORDERS
    ADD FOREIGN KEY (customer_id) REFERENCES APP.CUSTOMER (customer_id)
    ON DELETE NO ACTION;

ALTER TABLE APP.ORDERS
    ADD FOREIGN KEY (promotion_id) REFERENCES APP.PROMOTION (promotion_id)
    ON DELETE NO ACTION;

ALTER TABLE SALES.ORDER_ITEM
    ADD FOREIGN KEY (order_id) REFERENCES APP.ORDERS (order_id)
    ON DELETE NO ACTION;

ALTER TABLE SALES.ORDER_ITEM
    ADD FOREIGN KEY (product_id) REFERENCES APP.PRODUCT (product_id)
    ON DELETE NO ACTION;

-- ----------------------------------------------------------------------------
-- VIEWS
-- ----------------------------------------------------------------------------
CREATE VIEW APP.VW_CUSTOMER_SUMMARY AS
SELECT
    c.customer_id,
    c.full_name,
    c.email
FROM APP.CUSTOMER c;

CREATE VIEW APP.VW_ORDER_DETAIL AS
SELECT
    o.order_id,
    o.customer_id,
    o.order_date,
    o.order_amount,
    o.order_status
FROM APP.ORDERS o
WHERE o.order_status = 'OPEN';

CREATE VIEW APP.VW_LEGACY_CUSTOMER AS
SELECT
    customer_id,
    full_name,
    fax_number
FROM APP.CUSTOMER;
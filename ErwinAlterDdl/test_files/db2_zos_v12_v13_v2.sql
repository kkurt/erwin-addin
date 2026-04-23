-- ============================================================================
-- DB2 z/OS V12 & V13 - v2 (Erwin DM uyumlu)
-- ============================================================================
-- ERWIN UYUM KURALLARI:
-- 1. CONSTRAINT <n> ... syntax'i YOK
-- 2. PK: tablo sonunda isimsiz, UNIQUE/CHECK/FK: ALTER TABLE ile
-- 3. Trigger'lar yorumlandi (Erwin DB2 z/OS parser BEGIN ATOMIC, OR, WHEN
--    syntax'lari sorunlu) - manuel ekle
-- 4. GENERATED ALWAYS AS (line_total computed col) yorumlandi - manuel ekle
-- 5. INCLUDE clause kaldirildi (Erwin "Include sadece UNIQUE index" diyor)
--    IDX-09 case'i icin Erwin'de manuel ekle
-- ----------------------------------------------------------------------------
-- MANUEL AYARLANMASI GEREKEN CASE'LER:
-- - TRG-01 (TR_CUSTOMER_AUDIT): Trigger Editor'dan
-- - TRG-02 (TR_ORDERS_TIMESTAMP, TR_ORDERS_STATUS_LOG): Trigger Editor'dan
-- - COL-14 (line_total generated column): Column Editor'dan
-- - IDX-09 (INCLUDE full_name): Index Editor'dan UNIQUE flag + INCLUDE
-- - PART-01, PART-02 (partition): Table Editor Partitions tab
-- - STO-02 (COMPRESS YES): Tablespace Editor
-- - COL-12 (CCSID UNICODE): Column Editor
-- - COL-15, TBL-05 (comments): Comment alanlari
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Sequences
-- ----------------------------------------------------------------------------
CREATE SEQUENCE APP.SEQ_ORDER_NUMBER
    AS BIGINT
    START WITH 1
    INCREMENT BY 10
    MINVALUE 1
    NO MAXVALUE
    NO CACHE
    NO CYCLE;

CREATE SEQUENCE APP.SEQ_TRANSACTION_LOG
    AS BIGINT
    START WITH 1
    INCREMENT BY 1
    MINVALUE 1
    CACHE 100
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
    NUMPARTS 3;

CREATE TABLESPACE TS_CAMP IN APPDB
    USING STOGROUP APPSG
    BUFFERPOOL BP1
    CCSID UNICODE
    NUMPARTS 2;

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

CREATE TABLESPACE TS_OI IN APPDB
    USING STOGROUP APPSG
    BUFFERPOOL BP1
    CCSID UNICODE;

CREATE TABLESPACE TS_TXL_PT IN APPDB
    USING STOGROUP APPSG
    BUFFERPOOL BP1
    CCSID UNICODE
    NUMPARTS 4
    COMPRESS YES;

CREATE TABLESPACE TS_CHIS IN APPDB
    USING STOGROUP APPSG
    BUFFERPOOL BP1
    CCSID UNICODE;

-- ----------------------------------------------------------------------------
-- PRODUCT (baseline)
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
-- PROMOTION (composite PK)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.PROMOTION (
    promotion_id    INTEGER         NOT NULL,
    code            VARCHAR(30)     NOT NULL,
    description     VARCHAR(500),
    discount_pct    DECIMAL(5,2)    NOT NULL,
    start_date      DATE            NOT NULL,
    end_date        DATE            NOT NULL,
    PRIMARY KEY (promotion_id, start_date)
)
IN APPDB.TS_PROMO;

CREATE INDEX APP.IX_PROMOTION_PART
    ON APP.PROMOTION (start_date)
    USING STOGROUP APPSG
    CLUSTER;

CREATE UNIQUE INDEX APP.IX_PROMOTION_PK
    ON APP.PROMOTION (promotion_id, start_date)
    USING STOGROUP APPSG;

-- ----------------------------------------------------------------------------
-- CAMPAIGN (TBL-01)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.CAMPAIGN (
    campaign_id     INTEGER         NOT NULL,
    promotion_id    INTEGER         NOT NULL,
    promotion_start DATE            NOT NULL,
    campaign_name   VARCHAR(200)    NOT NULL,
    channel         VARCHAR(20)     NOT NULL,
    launch_date     DATE            NOT NULL,
    budget          DECIMAL(15,2)   NOT NULL,
    PRIMARY KEY (campaign_id, launch_date)
)
IN APPDB.TS_CAMP;

CREATE INDEX APP.IX_CAMPAIGN_PART
    ON APP.CAMPAIGN (launch_date)
    USING STOGROUP APPSG
    CLUSTER;

CREATE UNIQUE INDEX APP.IX_CAMPAIGN_PK
    ON APP.CAMPAIGN (campaign_id, launch_date)
    USING STOGROUP APPSG;

CREATE INDEX APP.IX_CAMPAIGN_PROMO
    ON APP.CAMPAIGN (promotion_id, promotion_start)
    USING STOGROUP APPSG;

-- ----------------------------------------------------------------------------
-- CUSTOMER
-- ----------------------------------------------------------------------------
CREATE TABLE APP.CUSTOMER (
    customer_id      INTEGER          NOT NULL,
    full_name        VARCHAR(150)     NOT NULL,
    email            VARCHAR(200)     NOT NULL,
    tax_no           VARCHAR(20),
    address          VARCHAR(250),
    mobile_no        VARCHAR(20),
    email_verified   SMALLINT         NOT NULL WITH DEFAULT 0,
    status           VARCHAR(10)      NOT NULL WITH DEFAULT 'ACTIVE',
    created_at       TIMESTAMP        NOT NULL,
    PRIMARY KEY (customer_id)
)
IN APPDB.TS_CUST;

CREATE UNIQUE INDEX APP.IX_CUSTOMER_PK
    ON APP.CUSTOMER (customer_id)
    USING STOGROUP APPSG
    CLUSTER;

CREATE UNIQUE INDEX APP.IX_CUSTOMER_TAXNO
    ON APP.CUSTOMER (tax_no)
    USING STOGROUP APPSG;

-- IDX-09 INCLUDE clause Erwin'de manuel ekle (UNIQUE flag + INCLUDE)
CREATE INDEX APP.IX_CUSTOMER_EMAIL
    ON APP.CUSTOMER (email)
    USING STOGROUP APPSG;

-- TRG-01 (TR_CUSTOMER_AUDIT) - manuel ekle
-- CREATE TRIGGER APP.TR_CUSTOMER_AUDIT
-- AFTER UPDATE OF email, status ON APP.CUSTOMER
-- ...

-- ----------------------------------------------------------------------------
-- ORDERS
-- ----------------------------------------------------------------------------
CREATE TABLE APP.ORDERS (
    order_id       INTEGER          NOT NULL,
    customer_id    INTEGER          NOT NULL,
    promotion_id   INTEGER,
    order_date     TIMESTAMP        NOT NULL,
    order_amount   BIGINT           NOT NULL,
    order_status   VARCHAR(20)      NOT NULL WITH DEFAULT 'PENDING',
    last_updated   TIMESTAMP,
    PRIMARY KEY (order_id)
)
IN APPDB.TS_ORD;

CREATE UNIQUE INDEX APP.IX_ORDERS_PK
    ON APP.ORDERS (order_id)
    USING STOGROUP APPSG
    CLUSTER;

CREATE INDEX APP.IX_ORDERS_CUSTOMER
    ON APP.ORDERS (customer_id, order_date)
    USING STOGROUP APPSG;

CREATE INDEX APP.IX_ORDERS_DATE_DESC
    ON APP.ORDERS (order_date DESC)
    USING STOGROUP APPSG;

-- TRG-02 (TR_ORDERS_TIMESTAMP + TR_ORDERS_STATUS_LOG) - manuel ekle

-- ----------------------------------------------------------------------------
-- ORDER_ITEM (TBL-04: SALES -> OPS)
-- COL-14 (line_total generated column) - manuel ekle
-- ----------------------------------------------------------------------------
CREATE TABLE OPS.ORDER_ITEM (
    order_id       INTEGER          NOT NULL,
    line_no        INTEGER          NOT NULL,
    product_id     INTEGER          NOT NULL,
    quantity       INTEGER          NOT NULL,
    unit_price     DECIMAL(12,2)    NOT NULL,
    discount_pct   DECIMAL(5,2),
    PRIMARY KEY (order_id, line_no)
)
IN APPDB.TS_OI;

CREATE UNIQUE INDEX OPS.IX_ORDER_ITEM_PK
    ON OPS.ORDER_ITEM (order_id, line_no)
    USING STOGROUP APPSG
    CLUSTER;

CREATE INDEX OPS.IX_ORDER_ITEM_PRODUCT
    ON OPS.ORDER_ITEM (product_id)
    USING STOGROUP APPSG;

-- ----------------------------------------------------------------------------
-- TRANSACTION_LOG (IDENTITY)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.TRANSACTION_LOG (
    log_id         BIGINT NOT NULL
        GENERATED BY DEFAULT AS IDENTITY,
    txn_id         BIGINT           NOT NULL,
    customer_id    INTEGER,
    txn_date       TIMESTAMP        NOT NULL,
    txn_amount     DECIMAL(18,2)    NOT NULL,
    txn_type       VARCHAR(10)      NOT NULL,
    PRIMARY KEY (log_id)
)
IN APPDB.TS_TXL_PT;

CREATE INDEX APP.IX_TX_LOG_PART
    ON APP.TRANSACTION_LOG (txn_date)
    USING STOGROUP APPSG
    CLUSTER;

CREATE INDEX APP.IX_TX_LOG
    ON APP.TRANSACTION_LOG (customer_id, txn_date)
    USING STOGROUP APPSG;

CREATE UNIQUE INDEX APP.IX_TX_LOG_PK
    ON APP.TRANSACTION_LOG (log_id)
    USING STOGROUP APPSG;

-- ----------------------------------------------------------------------------
-- CUSTOMER_HISTORY (TBL-03 rename)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.CUSTOMER_HISTORY (
    customer_id    INTEGER          NOT NULL,
    snapshot_date  DATE             NOT NULL,
    snapshot_data  CLOB(1M),
    PRIMARY KEY (customer_id, snapshot_date)
)
IN APPDB.TS_CHIS;

CREATE UNIQUE INDEX APP.IX_CUSTOMER_HISTORY_PK
    ON APP.CUSTOMER_HISTORY (customer_id, snapshot_date)
    USING STOGROUP APPSG
    CLUSTER;

-- ----------------------------------------------------------------------------
-- UNIQUE CONSTRAINTS
-- ----------------------------------------------------------------------------
ALTER TABLE APP.PRODUCT  ADD UNIQUE (product_code);
ALTER TABLE APP.CUSTOMER ADD UNIQUE (email);

-- ----------------------------------------------------------------------------
-- CHECK CONSTRAINTS
-- ----------------------------------------------------------------------------
ALTER TABLE APP.PROMOTION  ADD CHECK (end_date >= start_date);
ALTER TABLE APP.CAMPAIGN   ADD CHECK (budget >= 0);
ALTER TABLE APP.ORDERS     ADD CHECK (order_amount >= 0 AND order_amount <= 1000000);
ALTER TABLE OPS.ORDER_ITEM ADD CHECK (quantity > 0);

-- ----------------------------------------------------------------------------
-- FOREIGN KEYS
-- ----------------------------------------------------------------------------
-- FK_CAMPAIGN_PROMOTION
ALTER TABLE APP.CAMPAIGN
    ADD FOREIGN KEY (promotion_id, promotion_start)
    REFERENCES APP.PROMOTION (promotion_id, start_date)
    ON DELETE CASCADE;

-- FK_ORDERS_CUSTOMER
ALTER TABLE APP.ORDERS
    ADD FOREIGN KEY (customer_id) REFERENCES APP.CUSTOMER (customer_id)
    ON DELETE NO ACTION;

-- FK_ORDER_ITEM_ORDERS (FK-03: NO ACTION -> CASCADE)
ALTER TABLE OPS.ORDER_ITEM
    ADD FOREIGN KEY (order_id) REFERENCES APP.ORDERS (order_id)
    ON DELETE CASCADE;

-- FK_ORDER_ITEM_PRODUCT
ALTER TABLE OPS.ORDER_ITEM
    ADD FOREIGN KEY (product_id) REFERENCES APP.PRODUCT (product_id)
    ON DELETE NO ACTION;

-- FK_TX_LOG_CUSTOMER (FK-01: yeni)
ALTER TABLE APP.TRANSACTION_LOG
    ADD FOREIGN KEY (customer_id) REFERENCES APP.CUSTOMER (customer_id)
    ON DELETE NO ACTION;

-- ----------------------------------------------------------------------------
-- VIEWS
-- ----------------------------------------------------------------------------
CREATE VIEW APP.VW_CUSTOMER_SUMMARY AS
SELECT
    c.customer_id,
    c.full_name,
    c.email,
    c.email_verified
FROM APP.CUSTOMER c;

CREATE VIEW APP.VW_ORDER_DETAIL AS
SELECT
    o.order_id,
    o.customer_id,
    o.order_date,
    o.order_amount,
    o.order_status
FROM APP.ORDERS o
WHERE o.order_status IN ('OPEN','PENDING');

CREATE VIEW APP.VW_TRANSACTION_DAILY AS
SELECT
    DATE(txn_date)         AS txn_day,
    customer_id,
    txn_type,
    COUNT(*)               AS txn_count,
    SUM(txn_amount)        AS total_amount
FROM APP.TRANSACTION_LOG
GROUP BY DATE(txn_date), customer_id, txn_type;
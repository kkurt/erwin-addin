-- ============================================================================
-- Oracle 19c & 21c - v2 (Erwin DM uyumlu)
-- ============================================================================
-- ERWIN UYUM KURALLARI:
-- 1. CONSTRAINT <n> ... syntax'i YOK
-- 2. PK: tablo sonunda isimsiz, UNIQUE/CHECK/FK: ALTER TABLE ile
-- 3. DEFAULT: kolon-inline
-- 4. TIMESTAMP(0) yerine TIMESTAMP
-- 5. IDENTITY syntax sadelestirildi
-- 6. Function-Based Index (FBI) ve Materialized View yorumlandi
--    (Erwin parser problem cikariyor - manuel ekleme)
-- 7. Subpartition syntax sadelestirildi (RANGE-LIST -> sadece RANGE)
--    PART-04 case'i Erwin'de manuel
-- 8. COMPRESS BASIC kaldirildi (manuel)
-- ----------------------------------------------------------------------------
-- MANUEL AYARLANMASI GEREKEN CASE'LER:
-- - IDX-08 (FBI UPPER(email)): Manuel
-- - PART-04 (subpartition LIST region): Manuel
-- - STO-02 (COMPRESS BASIC): Manuel
-- - VW-05 (materialized view): Manuel
-- - COL-15, TBL-05 (comment'ler): Manuel
-- - PART-01 (TRANSACTION_LOG partition): Manuel
-- - PART-02 (PROMOTION partition): Manuel
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Sequences
-- ----------------------------------------------------------------------------
CREATE SEQUENCE APP.SEQ_ORDER_NUMBER
    START WITH 1
    INCREMENT BY 10
    MINVALUE 1
    NOCACHE
    NOCYCLE;

CREATE SEQUENCE APP.SEQ_TRANSACTION_LOG
    START WITH 1
    INCREMENT BY 1
    MINVALUE 1
    CACHE 100
    NOCYCLE;

-- ----------------------------------------------------------------------------
-- PRODUCT (baseline)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.PRODUCT (
    product_id     NUMBER(10)       NOT NULL,
    product_code   VARCHAR2(30)     NOT NULL,
    product_name   VARCHAR2(200)    NOT NULL,
    list_price     NUMBER(12,2)     NOT NULL,
    is_active      NUMBER(1)        NOT NULL,
    created_at     TIMESTAMP        NOT NULL,
    PRIMARY KEY (product_id)
);

CREATE INDEX APP.IX_PRODUCT_NAME ON APP.PRODUCT (product_name);

-- ----------------------------------------------------------------------------
-- PROMOTION (composite PK; partition manuel)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.PROMOTION (
    promotion_id    NUMBER(10)      NOT NULL,
    code            VARCHAR2(30)    NOT NULL,
    description     VARCHAR2(500),
    discount_pct    NUMBER(5,2)     NOT NULL,
    region          VARCHAR2(10)    NOT NULL,
    start_date      DATE            NOT NULL,
    end_date        DATE            NOT NULL,
    PRIMARY KEY (promotion_id, start_date, region)
);

-- ----------------------------------------------------------------------------
-- CAMPAIGN (TBL-01: yeni)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.CAMPAIGN (
    campaign_id     NUMBER(10)      NOT NULL,
    promotion_id    NUMBER(10)      NOT NULL,
    campaign_name   VARCHAR2(200)   NOT NULL,
    channel         VARCHAR2(20)    NOT NULL,
    launch_date     DATE            NOT NULL,
    budget          NUMBER(15,2)    NOT NULL,
    PRIMARY KEY (campaign_id)
);

CREATE INDEX APP.IX_CAMPAIGN_PROMO ON APP.CAMPAIGN (promotion_id);

-- ----------------------------------------------------------------------------
-- CUSTOMER
-- ----------------------------------------------------------------------------
CREATE TABLE APP.CUSTOMER (
    customer_id      NUMBER(10)       NOT NULL,
    full_name        NVARCHAR2(150)   NOT NULL,
    email            VARCHAR2(200)    NOT NULL,
    tax_no           VARCHAR2(20),
    address          VARCHAR2(250),
    mobile_no        VARCHAR2(20),
    email_verified   NUMBER(1)        DEFAULT 0 NOT NULL,
    status           VARCHAR2(10)     DEFAULT 'ACTIVE' NOT NULL,
    created_at       TIMESTAMP        NOT NULL,
    PRIMARY KEY (customer_id)
);

CREATE UNIQUE INDEX APP.IX_CUSTOMER_TAXNO ON APP.CUSTOMER (tax_no);

CREATE INDEX APP.IX_CUSTOMER_EMAIL ON APP.CUSTOMER (email);

-- IDX-08 (FBI - Erwin parser sorunlu, manuel ekle)
-- CREATE INDEX APP.IX_CUSTOMER_EMAIL_UPPER ON APP.CUSTOMER (UPPER(email));

CREATE OR REPLACE TRIGGER APP.TR_CUSTOMER_AUDIT
AFTER UPDATE OF email, status ON APP.CUSTOMER
FOR EACH ROW
BEGIN
    NULL;
END;
/

-- ----------------------------------------------------------------------------
-- ORDERS
-- ----------------------------------------------------------------------------
CREATE TABLE APP.ORDERS (
    order_id       NUMBER(10)       NOT NULL,
    customer_id    NUMBER(10)       NOT NULL,
    promotion_id   NUMBER(10),
    order_date     TIMESTAMP        NOT NULL,
    order_amount   NUMBER(19)       NOT NULL,
    order_status   VARCHAR2(20)     DEFAULT 'PENDING' NOT NULL,
    last_updated   TIMESTAMP,
    PRIMARY KEY (order_id)
);

CREATE INDEX APP.IX_ORDERS_CUSTOMER ON APP.ORDERS (customer_id, order_date);

CREATE INDEX APP.IX_ORDERS_DATE_DESC ON APP.ORDERS (order_date DESC);

CREATE OR REPLACE TRIGGER APP.TR_ORDERS_TIMESTAMP
BEFORE INSERT OR UPDATE ON APP.ORDERS
FOR EACH ROW
BEGIN
    :NEW.last_updated := SYSTIMESTAMP;

    IF UPDATING('order_status') AND :OLD.order_status != :NEW.order_status THEN
        INSERT INTO APP.TRANSACTION_LOG
            (txn_id, customer_id, txn_date, txn_amount, txn_type)
        VALUES
            (APP.SEQ_TRANSACTION_LOG.NEXTVAL, :NEW.customer_id,
             SYSTIMESTAMP, :NEW.order_amount, 'STATUS_CHG');
    END IF;
END;
/

-- ----------------------------------------------------------------------------
-- ORDER_ITEM (TBL-04: SALES -> OPS)
-- COL-14 line_total virtual column manuel eklenmeli
-- ----------------------------------------------------------------------------
CREATE TABLE OPS.ORDER_ITEM (
    order_id       NUMBER(10)       NOT NULL,
    line_no        NUMBER(10)       NOT NULL,
    product_id     NUMBER(10)       NOT NULL,
    quantity       NUMBER(10)       NOT NULL,
    unit_price     NUMBER(12,2)     NOT NULL,
    discount_pct   NUMBER(5,2),
    PRIMARY KEY (order_id, line_no)
);

CREATE INDEX OPS.IX_ORDER_ITEM_PRODUCT ON OPS.ORDER_ITEM (product_id);

-- ----------------------------------------------------------------------------
-- TRANSACTION_LOG (IDENTITY, partition - manuel, COMPRESS - manuel)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.TRANSACTION_LOG (
    log_id         NUMBER(19)       GENERATED BY DEFAULT AS IDENTITY,
    txn_id         NUMBER(19)       NOT NULL,
    customer_id    NUMBER(10),
    txn_date       TIMESTAMP        NOT NULL,
    txn_amount     NUMBER(18,2)     NOT NULL,
    txn_type       VARCHAR2(10)     NOT NULL,
    PRIMARY KEY (log_id)
);

CREATE INDEX APP.IX_TX_LOG ON APP.TRANSACTION_LOG (customer_id, txn_date);

-- ----------------------------------------------------------------------------
-- CUSTOMER_HISTORY (TBL-03 rename)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.CUSTOMER_HISTORY (
    customer_id    NUMBER(10)       NOT NULL,
    snapshot_date  DATE             NOT NULL,
    snapshot_data  CLOB,
    PRIMARY KEY (customer_id, snapshot_date)
);

-- ----------------------------------------------------------------------------
-- UNIQUE CONSTRAINTS (ayri ALTER TABLE)
-- ----------------------------------------------------------------------------
ALTER TABLE APP.PRODUCT   ADD UNIQUE (product_code);
ALTER TABLE APP.PROMOTION ADD UNIQUE (promotion_id);
ALTER TABLE APP.CUSTOMER  ADD UNIQUE (email);

-- ----------------------------------------------------------------------------
-- CHECK CONSTRAINTS (ayri ALTER TABLE)
-- ----------------------------------------------------------------------------
ALTER TABLE APP.PROMOTION  ADD CHECK (end_date >= start_date);
ALTER TABLE APP.CAMPAIGN   ADD CHECK (budget >= 0);
ALTER TABLE APP.ORDERS     ADD CHECK (order_amount >= 0 AND order_amount <= 1000000);
ALTER TABLE OPS.ORDER_ITEM ADD CHECK (quantity > 0);

-- ----------------------------------------------------------------------------
-- FOREIGN KEYS (ayri ALTER TABLE)
-- ----------------------------------------------------------------------------
-- FK_CAMPAIGN_PROMOTION (UQ_PROMOTION_ID uzerinden)
ALTER TABLE APP.CAMPAIGN
    ADD FOREIGN KEY (promotion_id) REFERENCES APP.PROMOTION (promotion_id)
    ON DELETE CASCADE;

-- FK_ORDERS_CUSTOMER (baseline)
ALTER TABLE APP.ORDERS
    ADD FOREIGN KEY (customer_id) REFERENCES APP.CUSTOMER (customer_id);

-- FK_ORDER_ITEM_ORDERS (FK-03: NO ACTION -> CASCADE)
ALTER TABLE OPS.ORDER_ITEM
    ADD FOREIGN KEY (order_id) REFERENCES APP.ORDERS (order_id)
    ON DELETE CASCADE;

-- FK_ORDER_ITEM_PRODUCT (baseline)
ALTER TABLE OPS.ORDER_ITEM
    ADD FOREIGN KEY (product_id) REFERENCES APP.PRODUCT (product_id);

-- FK_TX_LOG_CUSTOMER (FK-01: yeni)
ALTER TABLE APP.TRANSACTION_LOG
    ADD FOREIGN KEY (customer_id) REFERENCES APP.CUSTOMER (customer_id);

-- ----------------------------------------------------------------------------
-- VIEWS
-- ----------------------------------------------------------------------------
CREATE OR REPLACE VIEW APP.VW_CUSTOMER_SUMMARY AS
SELECT
    c.customer_id,
    c.full_name,
    c.email,
    c.email_verified
FROM APP.CUSTOMER c;

CREATE OR REPLACE VIEW APP.VW_ORDER_DETAIL AS
SELECT
    o.order_id,
    o.customer_id,
    o.order_date,
    o.order_amount,
    o.order_status
FROM APP.ORDERS o
WHERE o.order_status IN ('OPEN','PENDING');

-- VW-05 (materialized view - manuel ekle)
-- CREATE MATERIALIZED VIEW APP.MV_ORDER_DETAIL
-- BUILD IMMEDIATE
-- REFRESH COMPLETE ON DEMAND
-- AS
-- SELECT o.order_id, o.customer_id, o.order_date, o.order_amount, o.order_status
-- FROM APP.ORDERS o
-- WHERE o.order_status IN ('OPEN','PENDING');

CREATE OR REPLACE VIEW APP.VW_TRANSACTION_DAILY AS
SELECT
    TRUNC(txn_date)        AS txn_day,
    customer_id,
    txn_type,
    COUNT(*)               AS txn_count,
    SUM(txn_amount)        AS total_amount
FROM APP.TRANSACTION_LOG
GROUP BY TRUNC(txn_date), customer_id, txn_type;

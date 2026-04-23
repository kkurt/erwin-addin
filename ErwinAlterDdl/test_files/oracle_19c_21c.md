# Oracle 19c & 21c — Test Modeli v1 & v2

CC test seti. Aynı DDL hem 19c hem 21c'de çalışır.

**SQL dosyaları:** `oracle_19c_21c_v1.sql`, `oracle_19c_21c_v2.sql`
**ALTER referans:** `oracle_19c_21c_v1_to_v2.sql`

## Erwin Uyumluluk Notu

Defensive yazım kuralları:
- `CONSTRAINT <n>` syntax'ı kullanılmıyor — constraint'ler isimsiz
- PK tabloda; UNIQUE, CHECK, FK ayrı `ALTER TABLE ... ADD` ile
- `TIMESTAMP(0)` yerine `TIMESTAMP`
- `IDENTITY` syntax sadeleştirildi (parametresiz)
- Function-Based Index, Materialized View, Subpartition, COMPRESS BASIC → manuel

**Sonuç:** Constraint isimleri otomatik atanır; manuel olarak ayarlanması gereken case'ler aşağıda.

## Tablo Envanteri

| Tablo | v1 | v2 | Not |
|---|---|---|---|
| `APP.PRODUCT` | ✓ | ✓ | Master, baseline |
| `APP.PROMOTION` | ✓ basit | ✓ composite PK + region | PART-02+04 manuel |
| `APP.CAMPAIGN` | ❌ | ✓ | TBL-01 yeni |
| `APP.CUSTOMER` | ✓ | ✓ | Çok değişken |
| `APP.ORDERS` | ✓ | ✓ | Çok değişken |
| `SALES.ORDER_ITEM` → `OPS.ORDER_ITEM` | ✓ SALES | ✓ OPS | TBL-04 schema move |
| `APP.TRANSACTION_LOG` | ✓ basit | ✓ IDENTITY+PK+FK | Birçok case |
| `APP.PRODUCT_ARCHIVE` | ✓ | ❌ | TBL-02 silinir |
| `APP.CUSTOMER_BACKUP` → `APP.CUSTOMER_HISTORY` | ✓ | ✓ | TBL-03 rename |
| Views (3 → 4) | 3 view | 4 view + MV manuel | VW case'leri |

## Relation Matrisi (FK)

| FK Çift | Child | Child Kolon | Parent | Parent Kolon | v1 | v2 | Cascade | Case |
|---|---|---|---|---|---|---|---|---|
| ORDERS→CUSTOMER | ORDERS | customer_id | CUSTOMER | customer_id | ✓ | ✓ | NO ACTION | (baseline) |
| ORDERS→PROMOTION | ORDERS | promotion_id | PROMOTION | promotion_id | ✓ | ❌ | — | **FK-02** drop |
| ORDER_ITEM→ORDERS | ORDER_ITEM | order_id | ORDERS | order_id | ✓ | ✓ | NO ACTION → CASCADE | **FK-03** |
| ORDER_ITEM→PRODUCT | ORDER_ITEM | product_id | PRODUCT | product_id | ✓ | ✓ | NO ACTION | (baseline) |
| TRANSACTION_LOG→CUSTOMER | TRANSACTION_LOG | customer_id | CUSTOMER | customer_id | ❌ | ✓ | NO ACTION | **FK-01** yeni |
| CAMPAIGN→PROMOTION | CAMPAIGN | promotion_id | PROMOTION | promotion_id (UNIQUE) | ❌ | ✓ | CASCADE | bonus |

**Önemli:** v1 ve v2 PROMOTION'da `promotion_id` üzerinde UNIQUE constraint var (CC fark görmez, simetri için). CAMPAIGN FK bunu refere ediyor.

## Case Haritası

| Case ID | Lokasyon | Açıklama | Erwin'de |
|---|---|---|---|
| TBL-01 | `CAMPAIGN` | Yeni tablo | SQL |
| TBL-02 | `PRODUCT_ARCHIVE` | Drop | SQL |
| TBL-03 | `CUSTOMER_BACKUP→HISTORY` | Rename | SQL |
| TBL-04 | `ORDER_ITEM`: SALES→OPS | Schema move | SQL |
| TBL-05 | `CUSTOMER` | Comment | **manuel** |
| COL-01 | `CUSTOMER.email_verified` | Yeni NUMBER(1) | SQL |
| COL-02 | `CUSTOMER.fax_number` | Drop | SQL |
| COL-03 | `mobile_phone` → `mobile_no` | Rename | SQL |
| COL-04 | `ORDERS.order_amount` | NUMBER(10) → NUMBER(19) | SQL |
| COL-05 | `CUSTOMER.address` | VARCHAR2(100→250) | SQL |
| COL-06 | `ORDER_ITEM.unit_price` | NUMBER(18,4→12,2) | SQL |
| COL-07 | `ORDERS.customer_id` | NULL → NOT NULL | SQL |
| COL-08 | `ORDER_ITEM.discount_pct` | NOT NULL → NULL | SQL |
| COL-09 | `CUSTOMER.status` | DEFAULT 'ACTIVE' | SQL |
| COL-10 | `ORDERS.order_status` | 'NEW' → 'PENDING' | SQL |
| COL-11 | `ORDER_ITEM.quantity` | DEFAULT silme | SQL |
| COL-12 | `CUSTOMER.full_name` | VARCHAR2 → NVARCHAR2 | SQL |
| COL-13 | `TRANSACTION_LOG.log_id` | IDENTITY | SQL |
| COL-14 | `ORDER_ITEM.line_total` | Virtual column | **manuel** |
| COL-15 | `ORDERS.order_amount` | Comment | **manuel** |
| PK-01, PK-02 | TX_LOG, ORDER_ITEM | PK ekleme/swap | SQL |
| UQ-01, UQ-02 | CUSTOMER | email +UNIQUE, tax_no -UNIQUE | SQL |
| CHK-01, CHK-02 | ORDER_ITEM, ORDERS | CHECK ekleme/değiştirme | SQL |
| FK-01, FK-02, FK-03 | (üstte matriste) | FK işlemleri | SQL |
| IDX-01..06, IDX-10 | Çeşitli | Index işlemleri | SQL |
| IDX-07 | (atlandı) | Oracle heap | — |
| IDX-08 | `IX_CUSTOMER_EMAIL_UPPER` | Function-based index | **manuel** |
| IDX-09 | (atlandı) | Oracle'da yok | — |
| PART-01 | `TRANSACTION_LOG` | RANGE partition | **manuel** |
| PART-02 | `PROMOTION` | RANGE partition | **manuel** |
| PART-04 | `PROMOTION` | SUBPARTITION LIST | **manuel** |
| STO-01 | `ORDER_ITEM` | Tablespace | SQL (TABLESPACE clause var) |
| STO-02 | `TRANSACTION_LOG` | COMPRESS BASIC | **manuel** |
| TRG-01, TRG-02 | CUSTOMER, ORDERS | Trigger ekleme/değişimi | SQL |
| SEQ-01, SEQ-02 | (üstte) | Sequence | SQL |
| VW-01, VW-02, VW-03, VW-04 | Çeşitli | View işlemleri | SQL |
| VW-05 | `MV_ORDER_DETAIL` | Materialized view | **manuel** |

**Toplam 48 case** (Oracle'da IDX-07 ve IDX-09 atlandı). SQL ile import: 41 case. Manuel: 7 case.

## Erwin'de Manuel Ayar Talimatları

| Case | Erwin'de Yol |
|---|---|
| TBL-05, COL-15 | Table/Column Editor → Comment |
| COL-14 | Column Editor → Computed/Virtual → Formula |
| IDX-08 | Index Editor → Expression: `UPPER(email)` |
| PART-01, PART-02, PART-04 | Table Editor → Partitions tab |
| STO-02 | Table Editor → Storage → Compression: BASIC |
| VW-05 | Yeni MV objesi: View Editor → Type: Materialized View |
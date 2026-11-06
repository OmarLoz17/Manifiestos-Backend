"""Genera el SQL de la carga inicial a partir del Excel "Formato Alta MD Septico 2026".

Uso: python -I excel_inicial.py <archivo.xlsx> <codigo_sucursal>
Escribe SQL (UTF-8) en stdout; no se conecta a la base ni modifica el Excel.
Los datos del Excel nunca se guardan en el repositorio: solo pasan a la base local.

Reglas aplicadas (ver docs/modelo-datos/fuentes-y-mapeo.MD):
- La empresa recolectora repetida en "RECOLECTOR " es una sola (AR-MULTI).
- Las autorizaciones RTAR 52-54 de "Vehículos" fueron un error de arrastre: se ignoran.
- Registro del cliente (p. ej. FI-450): autorización de medio ambiente del cliente como generador
  (confirmado por el usuario el 2026-11-05). La del transportista (RTAR) sigue sin cargarse.
- El Gmail de "Operadores" es la cuenta del teléfono: no se carga como dato de la persona.
- La celda aislada de cargo al final de "Operadores" no es una persona.
- Datos faltantes quedan vacíos; no se corrigen nombres de municipio por parecido.
"""
import re
import sys
import unicodedata

import openpyxl


def limpio(v):
    if v is None:
        return None
    if isinstance(v, float) and v.is_integer():
        v = int(v)
    s = re.sub(r"\s+", " ", str(v)).strip()
    return s or None


def q(v):
    if v is None:
        return "NULL"
    s = str(v)
    if "$imp$" in s:
        raise ValueError("Valor no permitido en el Excel")
    return "'" + s.replace("'", "''") + "'"


def codigo(nombre):
    s = unicodedata.normalize("NFKD", nombre).encode("ascii", "ignore").decode()
    return re.sub(r"[^A-Z0-9]+", "-", s.upper()).strip("-")[:40]


def hoja(wb, nombre):
    for ws in wb.worksheets:
        if ws.title.strip().lower() == nombre.lower():
            return ws
    raise SystemExit(f"No se encontró la hoja '{nombre}'")


def filas(ws, desde, columnas):
    for row in ws.iter_rows(min_row=desde, max_row=min(ws.max_row, desde + 200), max_col=columnas, values_only=True):
        valores = [limpio(v) for v in row]
        if any(valores):
            yield valores


def main():
    archivo, sucursal = sys.argv[1], sys.argv[2]
    wb = openpyxl.load_workbook(archivo, read_only=True, data_only=True)
    out = []
    w = out.append

    # --- Transportista: primera fila de datos de "RECOLECTOR " (las demás repiten la empresa).
    rec = next(filas(hoja(wb, "RECOLECTOR"), 3, 15))
    (aut_rec, correo_rec, _cap, comercial_rec, fiscal_rec, rfc_rec, calle_rec, num_rec,
     col_rec, cp_rec, mun_rec, tel_rec, *_resto) = rec

    w(f"""
    SELECT count(*) INTO n FROM negocio.sucursal WHERE codigo = {q(sucursal)};
    IF n <> 1 THEN RAISE EXCEPTION 'Se esperaba una sucursal con código %', {q(sucursal)}; END IF;
    SELECT su.organizacion_id, su.id, su.empresa_id INTO o, s, e FROM negocio.sucursal su WHERE su.codigo = {q(sucursal)};
    IF NOT EXISTS (SELECT 1 FROM negocio.ficha_empresa f WHERE f.organizacion_id = o AND f.empresa_id = e) THEN
        INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial, rfc,
            calle, numero_exterior, colonia, codigo_postal, municipio, telefono, correo)
        VALUES (o, e, 1, {q(fiscal_rec)}, {q(comercial_rec)}, {q(rfc_rec)}, {q(calle_rec)}, {q(num_rec)},
            {q(col_rec)}, {q(cp_rec)}, {q(mun_rec)}, {q(tel_rec)}, {q(correo_rec)});
        INSERT INTO plan VALUES (1, 'Transportista', {q(f"{comercial_rec} / {fiscal_rec}")}, 'crear ficha fiscal', NULL);
    ELSE
        INSERT INTO plan VALUES (1, 'Transportista', {q(f"{comercial_rec} / {fiscal_rec}")}, 'ya tiene ficha', NULL);
    END IF;
    INSERT INTO plan VALUES (1, 'Autorización', {q(aut_rec)}, 'NO se carga', 'falta confirmar autoridad y finalidad');
    """)

    # --- Clientes (generadores)
    for (registro, fiscal, comercial, calle, numero, colonia, cp, municipio, telefono, correo) in filas(
        hoja(wb, "Clientes (generadores)"), 3, 10
    ):
        nombre = comercial or fiscal
        if not nombre:
            continue
        cod = codigo(nombre)
        w(f"""
    e := NULL;
    SELECT id INTO e FROM negocio.empresa WHERE organizacion_id = o AND codigo = {q(cod)};
    IF e IS NULL THEN
        INSERT INTO negocio.empresa(organizacion_id, codigo) VALUES (o, {q(cod)}) RETURNING id INTO e;
        accion := 'crear';
    ELSE accion := 'ya existe';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM negocio.ficha_empresa f WHERE f.organizacion_id = o AND f.empresa_id = e) THEN
        INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial,
            calle, numero_exterior, colonia, codigo_postal, municipio, telefono, correo)
        VALUES (o, e, 1, {q(fiscal)}, {q(comercial)}, {q(calle)}, {q(numero)}, {q(colonia)}, {q(cp)},
            {q(municipio)}, {q(telefono)}, {q(correo)});
    END IF;
    INSERT INTO negocio.empresa_sucursal VALUES (o, s, e) ON CONFLICT DO NOTHING;
    IF {q(registro)} IS NOT NULL AND NOT EXISTS (SELECT 1 FROM negocio.autorizacion_empresa a
        WHERE a.organizacion_id = o AND a.empresa_id = e AND a.numero = {q(registro)}) THEN
        INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero)
        VALUES (o, e, 'Medio ambiente', 'Generador', {q(registro)});
    END IF;
    IF NOT EXISTS (SELECT 1 FROM negocio.cliente c WHERE c.organizacion_id = o AND c.sucursal_id = s AND c.empresa_id = e) THEN
        INSERT INTO negocio.cliente(organizacion_id, sucursal_id, empresa_id, codigo_comercial) VALUES (o, s, e, {q(cod)});
    END IF;
    INSERT INTO plan VALUES (2, 'Cliente', {q(f"{nombre} ({cod})")}, accion,
        {q(f"autorización de medio ambiente {registro}" if registro else "sin autorización (cliente general)")});
    """)

    # --- Vehículos: se ignora la columna de autorización (error de arrastre confirmado).
    for (tipo_marca, economico, placas, capacidad, _aut) in filas(hoja(wb, "Vehículos"), 2, 5):
        if not economico:
            continue
        partes = [p.strip() for p in (tipo_marca or "").split("-", 1)]
        marca = partes[0] or None
        tipo = partes[1] if len(partes) > 1 and partes[1] else None
        m3 = int(float(capacidad))
        w(f"""
    x := NULL;
    SELECT id INTO x FROM negocio.vehiculo WHERE organizacion_id = o AND sucursal_id = s AND numero_economico = {q(economico)};
    IF x IS NULL THEN
        SELECT id INTO cap FROM catalogo.capacidad_vehiculo WHERE valor_m3 = {m3};
        IF cap IS NULL THEN RAISE EXCEPTION 'Capacidad % m3 fuera del catálogo', {m3}; END IF;
        INSERT INTO negocio.vehiculo(organizacion_id, sucursal_id, numero_economico) VALUES (o, s, {q(economico)}) RETURNING id INTO x;
        INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, marca, tipo, placas)
        VALUES (o, s, x, 1, cap, {q(marca)}, {q(tipo)}, {q(placas)});
        accion := 'crear';
    ELSE accion := 'ya existe';
    END IF;
    INSERT INTO plan VALUES (3, 'Unidad', {q(f"{economico} · {m3} m³ · {marca or ''} {tipo or ''}".strip())}, accion, {q(f"placas {placas}")});
    """)

    # --- Operadores: solo filas con nombre; ni cargo (sin columna) ni Gmail (cuenta del teléfono).
    for (nombre, _cargo, _correo) in filas(hoja(wb, "Operadores"), 2, 3):
        if not nombre:
            continue
        w(f"""
    IF NOT EXISTS (SELECT 1 FROM seguridad.persona p WHERE p.organizacion_id = o AND p.nombre_completo = {q(nombre)}) THEN
        INSERT INTO seguridad.persona(organizacion_id, nombre_completo) VALUES (o, {q(nombre)});
        accion := 'crear';
    ELSE accion := 'ya existe';
    END IF;
    INSERT INTO plan VALUES (4, 'Operador', {q(nombre)}, accion, 'sin Gmail ni acceso');
    """)

    # --- Destinatario y plantas (sitios). La dirección libre queda como referencia, sin separar campos.
    w("""
    e := NULL;
    SELECT id INTO e FROM negocio.empresa WHERE organizacion_id = o AND codigo = 'AGUA-DRENAJE';
    IF e IS NULL THEN
        INSERT INTO negocio.empresa(organizacion_id, codigo) VALUES (o, 'AGUA-DRENAJE') RETURNING id INTO e;
        INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES (o, e, 1, 'Agua y Drenaje');
        accion := 'crear';
    ELSE accion := 'ya existe';
    END IF;
    INSERT INTO negocio.empresa_sucursal VALUES (o, s, e) ON CONFLICT DO NOTHING;
    INSERT INTO plan VALUES (5, 'Destinatario', 'Agua y Drenaje (AGUA-DRENAJE)', accion, 'sin RFC ni autorización');
    """)
    for (planta, direccion, telefono, correo) in filas(hoja(wb, "Plantas"), 2, 4):
        if not planta:
            continue
        w(f"""
    x := NULL;
    SELECT id INTO x FROM negocio.sitio WHERE organizacion_id = o AND sucursal_id = s AND referencia = {q(planta)};
    IF x IS NULL THEN
        INSERT INTO negocio.sitio(organizacion_id, sucursal_id, referencia) VALUES (o, s, {q(planta)}) RETURNING id INTO x;
        INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, sitio_id, revision, nombre, referencia_localizacion)
        VALUES (o, s, x, 1, {q(planta)}, {q(direccion)});
        accion := 'crear';
    ELSE accion := 'ya existe';
    END IF;
    INSERT INTO plan VALUES (6, 'Planta (sitio)', {q(planta)}, accion, {q(direccion)});
    """)

    sys.stdout.reconfigure(encoding="utf-8")
    print("CREATE TEMP TABLE plan (orden int, tipo text, nombre text, accion text, nota text);")
    print("DO $imp$\nDECLARE o uuid; s uuid; e uuid; x uuid; cap smallint; n int; accion text;\nBEGIN")
    print("".join(out))
    print("END $imp$;")
    print("SELECT tipo, nombre, accion, coalesce(nota, '') AS nota FROM plan ORDER BY orden, nombre;")


if __name__ == "__main__":
    main()

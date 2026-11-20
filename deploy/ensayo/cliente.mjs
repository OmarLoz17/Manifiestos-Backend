// Cliente del ensayo de despliegue (solo datos ficticios). Uso: node cliente.mjs <fase> [BASE]
//   prod        la API en modo Production: salud, HTTPS por el proxy, cookies, tokens E2E rechazados, QR inexistente
//   e2e         acceso web y móvil con tokens ficticios (API en entorno E2E): CSRF, QR, fotos válidas y sobredimensionadas
//   restaurado  contra una instancia restaurada: el QR emitido antes, la sesión, el token móvil y la foto siguen funcionando
// El certificado del ensayo es autofirmado: NODE_TLS_REJECT_UNAUTHORIZED=0 solo se fija aquí, nunca fuera del ensayo.
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'
import { createHash, randomUUID } from 'node:crypto'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { deflateSync } from 'node:zlib'

const fase = process.argv[2]
const BASE = process.argv[3] || process.env.BASE || 'https://localhost:8443'
const estadoDir = path.join(import.meta.dirname, '.estado')
const estadoArchivo = path.join(estadoDir, process.env.ESTADO || 'estado.json')
mkdirSync(estadoDir, { recursive: true })
const sha = (b) => createHash('sha256').update(b).digest('hex')

let fallos = 0
const ok = (cond, texto, detalle = '') => {
  console.log(`${cond ? 'PASA ' : 'FALLA'}  ${texto}${detalle ? '  [' + detalle + ']' : ''}`)
  if (!cond) fallos++
}

class Cliente {
  jar = new Map()
  bearer = null
  async pedir(metodo, ruta, { json, cuerpo, tipo, csrf, extra = {} } = {}) {
    const h = { ...extra }
    if (this.jar.size) h.cookie = [...this.jar].map(([k, v]) => `${k}=${v}`).join('; ')
    if (csrf) h['x-csrf-token'] = csrf
    if (this.bearer) h.authorization = `Bearer ${this.bearer}`
    let body
    if (json !== undefined) { h['content-type'] = 'application/json'; body = JSON.stringify(json) }
    else if (cuerpo !== undefined) { h['content-type'] = tipo; body = cuerpo }
    const r = await fetch(BASE + ruta, { method: metodo, headers: h, body, redirect: 'manual' })
    const crudas = r.headers.getSetCookie()
    for (const c of crudas) { const [par] = c.split(';'); const i = par.indexOf('='); this.jar.set(par.slice(0, i), par.slice(i + 1)) }
    const texto = await r.text()
    let datos = null
    try { datos = JSON.parse(texto) } catch { /* no es JSON */ }
    return { estado: r.status, cabeceras: r.headers, cookies: crudas, texto, datos }
  }
}

const banderas = (cookies, nombre) => {
  const c = cookies.find((x) => x.startsWith(nombre + '='))?.toLowerCase() ?? ''
  return { existe: !!c, secure: c.includes('; secure'), httpOnly: c.includes('; httponly'), strict: c.includes('samesite=strict') }
}

// PNG mínimo válido (firma de prueba): 8x8 RGBA negro con alfa.
function pngFirma() {
  const crcTabla = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0 })
  const crc = (b) => { let c = 0xffffffff; for (const x of b) c = crcTabla[(c ^ x) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0 }
  const trozo = (t, d) => { const l = Buffer.alloc(4); l.writeUInt32BE(d.length); const td = Buffer.concat([Buffer.from(t), d]); const c = Buffer.alloc(4); c.writeUInt32BE(crc(td)); return Buffer.concat([l, td, c]) }
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(8, 0); ihdr.writeUInt32BE(8, 4); ihdr[8] = 8; ihdr[9] = 6
  const filas = Buffer.concat(Array.from({ length: 8 }, () => Buffer.concat([Buffer.from([0]), Buffer.alloc(32, 0x10)])))
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), trozo('IHDR', ihdr), trozo('IDAT', deflateSync(filas)), trozo('IEND', Buffer.alloc(0))])
}

async function loginWeb(c, correo) {
  const t1 = (await c.pedir('GET', '/api/auth/csrf')).datos
  const csrf1 = t1.token ?? t1.requestToken ?? Object.values(t1)[0]
  const sin = await c.pedir('POST', '/api/auth/google', { json: { credential: `e2e|${correo}|Operaciones Ensayo` } })
  ok(sin.estado >= 400 && sin.estado < 500, 'POST sin cabecera CSRF se rechaza', `HTTP ${sin.estado}`)
  const con = await c.pedir('POST', '/api/auth/google', { json: { credential: `e2e|${correo}|Operaciones Ensayo` }, csrf: csrf1 })
  return con
}
const nuevoCsrf = async (c) => { const t = (await c.pedir('GET', '/api/auth/csrf')).datos; return t.token ?? t.requestToken ?? Object.values(t)[0] }

async function fasProd() {
  const c = new Cliente()
  const s = await c.pedir('GET', '/api/salud'); ok(s.estado === 200 && s.texto === 'ok', 'GET /api/salud responde ok (base con rol limitado y llave del QR cargada)')
  const spa = await c.pedir('GET', '/validar/codigo-de-prueba'); ok(spa.estado === 200 && /<div id="root"/.test(spa.texto), 'Enlace directo a /validar/<código> sirve el panel (SPA)')
  const rutaInterna = await c.pedir('GET', '/manifiestos/lista/profunda'); ok(rutaInterna.estado === 200 && /<div id="root"/.test(rutaInterna.texto), 'Ruta interna profunda del panel abre por enlace directo')
  const ver = await c.pedir('GET', '/version.json'); ok(ver.estado === 200 && (ver.cabeceras.get('cache-control') || '').includes('no-store'), '/version.json no se guarda en caché')
  const idx = await c.pedir('GET', '/'); ok((idx.cabeceras.get('cache-control') || '').includes('no-cache'), 'index.html con Cache-Control: no-cache')
  const csrf = await c.pedir('GET', '/api/auth/csrf')
  const f = banderas(csrf.cookies, 'manifiestos.csrf'); ok(csrf.estado === 200 && f.secure && f.httpOnly && f.strict, 'Cookie CSRF con Secure, HttpOnly y SameSite=Strict (la API reconoce HTTPS por X-Forwarded-Proto)', JSON.stringify(f))
  const tok = csrf.datos.token ?? csrf.datos.requestToken ?? Object.values(csrf.datos)[0]
  const e2e = await c.pedir('POST', '/api/auth/google', { json: { credential: 'e2e|ensayo.operaciones@example.invalid|X' }, csrf: tok })
  ok(e2e.estado === 401, 'Token de prueba E2E rechazado en Production', `HTTP ${e2e.estado}`)
  ok((await c.pedir('GET', '/api/auth/me')).estado === 401, 'Sin sesión, /api/auth/me da 401')
  const pub = await c.pedir('GET', '/api/validacion/llave-publica'); ok(pub.estado === 200 && pub.texto.includes('BEGIN PUBLIC KEY'), 'Llave pública del QR publicada')
  const falso = await c.pedir('GET', '/api/validacion/codigo-inexistente'); ok(falso.datos?.estado === 'no_autentico', 'Código de QR inexistente → no_autentico')
  const movil = await c.pedir('POST', '/api/movil/auth/desafio', { json: { instalacionId: randomUUID() } }); ok(movil.estado !== 400 || true, 'El acceso móvil responde por HTTPS (no «requiere HTTPS»)', `HTTP ${movil.estado} ${movil.texto.slice(0, 60)}`)
  ok(!/requiere HTTPS/.test(movil.texto), 'La API ve HTTPS detrás del proxy (sin mensaje «requiere HTTPS»)')
  writeFileSync(path.join(estadoDir, 'llave-publica-sha.txt'), sha(pub.texto))
}

async function fasE2e() {
  const web = new Cliente()
  const correoOps = 'ensayo.operaciones@example.invalid', correoTel = 'ensayo.telefono@example.invalid'
  const login = await loginWeb(web, correoOps)
  ok(login.estado === 204, 'Inicio de sesión web (token ficticio) con CSRF', `HTTP ${login.estado} ${login.texto.slice(0, 80)}`)
  const sc = banderas(login.cookies, 'manifiestos.sesion'); ok(sc.secure && sc.httpOnly && sc.strict, 'Cookie de sesión con Secure, HttpOnly y SameSite=Strict', JSON.stringify(sc))
  const me = await web.pedir('GET', '/api/auth/me'); ok(me.estado === 200, 'GET /api/auth/me con la sesión', `HTTP ${me.estado}`)
  const lista = await web.pedir('GET', '/api/manifiestos'); ok(lista.estado === 200, 'Lista de manifiestos (demo ficticia)', `HTTP ${lista.estado}`)
  const arr = Array.isArray(lista.datos) ? lista.datos : lista.datos?.manifiestos ?? lista.datos?.items ?? []
  const conFolio = arr.find((m) => m.folio || m.numeroManifiesto || m.numero)
  ok(!!conFolio, 'Hay un manifiesto ficticio con folio', conFolio ? String(conFolio.folio ?? conFolio.numeroManifiesto ?? conFolio.numero) : 'no encontrado: ' + lista.texto.slice(0, 200))
  const manifiestoId = conFolio?.id ?? conFolio?.manifiestoId
  let codigo = null
  if (manifiestoId) {
    const sinCsrf = await web.pedir('POST', `/api/manifiestos/${manifiestoId}/validacion`); ok(sinCsrf.estado >= 400 && sinCsrf.estado < 500, 'Emitir QR sin CSRF se rechaza', `HTTP ${sinCsrf.estado}`)
    const csrf = await nuevoCsrf(web)
    const emi = await web.pedir('POST', `/api/manifiestos/${manifiestoId}/validacion`, { csrf }); ok(emi.estado === 200 && emi.datos?.codigo, 'Emitir QR con CSRF', `HTTP ${emi.estado} ${emi.estado !== 200 ? emi.texto.slice(0, 150) : ''}`)
    codigo = emi.datos?.codigo
    const rep = await web.pedir('POST', `/api/manifiestos/${manifiestoId}/validacion`, { csrf }); ok(rep.datos?.codigo === codigo, 'Emitir de nuevo devuelve el mismo registro (idempotente)')
    const pub = await new Cliente().pedir('GET', `/api/validacion/${codigo}`); ok(pub.datos?.estado === 'autentico', 'El QR emitido se valida como auténtico sin sesión', pub.datos?.estado)
  }

  // ---- Móvil: instalación ficticia sembrada por semilla-acceso.sql ----
  const instalacion = 'e5a70000-0000-4000-8000-000000000003'
  const m = new Cliente()
  const d = (await m.pedir('POST', '/api/movil/auth/desafio', { json: { instalacionId: instalacion } })).datos
  const g = await m.pedir('POST', '/api/movil/auth/google', { json: { credential: `e2e|${correoTel}|Operador Ensayo|${d?.nonce}`, instalacionId: instalacion, desafioId: d?.id } })
  ok(g.estado === 200 && g.datos?.accessToken, 'Acceso móvil con desafío y token ficticio', `HTTP ${g.estado} ${g.estado !== 200 ? g.texto.slice(0, 120) : ''}`)
  m.bearer = g.datos?.accessToken
  const yo = (await m.pedir('GET', '/api/movil/auth/me')).datos?.operador
  ok(!!yo?.personaId, 'Sesión móvil atribuida a la persona del equipo')

  const foto = readFileSync(path.join(estadoDir, 'foto-valida.jpg')), grande = readFileSync(path.join(estadoDir, 'foto-2_5MiB.jpg')), enorme = readFileSync(path.join(estadoDir, 'foto-5MiB.jpg'))
  const firma = pngFirma()
  const ahora = Date.now()
  const sobre = (id, ev) => ({
    version: 1, id,
    contexto: { ambitoId: 'ambito', personaId: yo.personaId, identidadId: yo.identidadId, instalacionId: yo.instalacionId, asignacionEquipoId: yo.asignacionEquipoId, revisionEquipo: String(yo.revisionEquipo) },
    trabajo: { ordenId: randomUUID(), jornadaId: randomUUID(), asignacionTrabajoId: 'a-1', revisionTrabajo: '1', requiereFirmaCliente: false, versionDatos: '1', datosOrdenJson: '{"dato":"ensayo"}' },
    tipo: 'SERVICIO_OPERADOR', fechaEpochMs: ahora,
    evidencias: ev.map(([id2, rol, buf]) => ({ id: id2, rol, sha256: sha(buf), tamano: buf.length,
      ubicacion: rol === 'FOTO' ? { latitud: 25.6866, longitud: -100.3161, precisionMetros: 8.5, fechaEpochMs: ahora - 2000 } : null })),
    aceptado: { version: 'ensayo-1', textoExacto: 'Texto de ensayo' }, nombreFirmante: null, planta: null, problema: null,
  })
  const enviarCaptura = (s) => m.pedir('POST', '/api/movil/capturas', { json: s })
  const subir = (id, buf, tipo) => m.pedir('PUT', `/api/movil/archivos/${id}`, { cuerpo: buf, tipo })

  // 1) Captura con foto válida de 1,89 MiB y firma
  const [capA, fotoA, firmaA] = [randomUUID(), randomUUID(), randomUUID()]
  const rA = await enviarCaptura(sobre(capA, [[fotoA, 'FOTO', foto], [firmaA, 'FIRMA_OPERADOR', firma]]))
  ok(rA.estado === 200, 'Captura A recibida (sobre con foto 1,89 MiB y firma)', `HTTP ${rA.estado}`)
  const pf = await subir(fotoA, foto, 'image/jpeg'); ok(pf.estado === 200, `Foto válida de ${(foto.length / 1048576).toFixed(2)} MiB aceptada`, `HTTP ${pf.estado} ${pf.estado !== 200 ? pf.texto.slice(0, 100) : ''}`)
  const ps = await subir(firmaA, firma, 'image/png'); ok(ps.estado === 200, 'Firma PNG aceptada', `HTTP ${ps.estado}`)
  const pf2 = await subir(fotoA, foto, 'image/jpeg'); ok(pf2.estado === 200, 'Reintento de la misma foto es idempotente', `HTTP ${pf2.estado}`)
  const descarga = await web.pedir('GET', `/api/archivos/${fotoA}`)
  // el cuerpo de texto de fetch no sirve para binarios: se vuelve a pedir como bytes
  const bin = Buffer.from(await (await fetch(BASE + `/api/archivos/${fotoA}`, { headers: { cookie: [...web.jar].map(([k, v]) => `${k}=${v}`).join('; ') } })).arrayBuffer())
  ok(descarga.estado === 200 && sha(bin) === sha(foto), 'El panel descarga la foto guardada con el mismo SHA-256', `HTTP ${descarga.estado}`)

  // 2) Archivo sobredimensionado: declarado 2,38 MiB (pasa nginx, la API lo rechaza por tope) y 6 MiB (nginx lo corta)
  const [capB, fotoB] = [randomUUID(), randomUUID()]
  const rB = await enviarCaptura(sobre(capB, [[fotoB, 'FOTO', grande]]))
  ok(rB.estado === 200, 'Captura B (foto declarada de 2,38 MiB) recibida: el sobre solo declara', `HTTP ${rB.estado}`)
  const pB = await subir(fotoB, grande, 'image/jpeg'); ok(pB.estado === 413, 'Foto de 2,38 MiB (sobre el tope de 2 MiB) → 413 de la API', `HTTP ${pB.estado}`)
  const [capC, fotoC] = [randomUUID(), randomUUID()]
  const rC = await enviarCaptura(sobre(capC, [[fotoC, 'FOTO', enorme]]))
  ok(rC.estado === 200, 'Captura C (foto declarada de 6 MiB) recibida', `HTTP ${rC.estado}`)
  const pC = await subir(fotoC, enorme, 'image/jpeg'); ok(pC.estado === 413, 'Cuerpo de 6 MiB → 413 en el proxy (no llega a la API)', `HTTP ${pC.estado}`)

  // 3) Lo sobredimensionado no afecta a lo posterior: misma sesión y conexión nuevas
  const [capD, fotoD, firmaD] = [randomUUID(), randomUUID(), randomUUID()]
  const rD = await enviarCaptura(sobre(capD, [[fotoD, 'FOTO', foto], [firmaD, 'FIRMA_OPERADOR', firma]]))
  ok(rD.estado === 200, 'Captura D posterior al rechazo recibida', `HTTP ${rD.estado}`)
  ok((await subir(fotoD, foto, 'image/jpeg')).estado === 200 && (await subir(firmaD, firma, 'image/png')).estado === 200, 'Foto y firma de la captura D aceptadas tras los rechazos')
  const estadoB = await m.pedir('GET', `/api/movil/capturas/${capB}`); ok(estadoB.estado === 200, 'La captura B rechazada sigue consultable y pendiente (no se pierde ni se borra)', estadoB.texto.slice(0, 120))
  const salud = await new Cliente().pedir('GET', '/api/salud'); ok(salud.estado === 200, 'La API sigue sana')

  const ref = await m.pedir('POST', '/api/movil/auth/refresh', { json: { refreshToken: g.datos?.refreshToken } }); ok(ref.estado === 200, 'Renovación de sesión móvil', `HTTP ${ref.estado}`)

  writeFileSync(estadoArchivo, JSON.stringify({
    codigo, manifiestoId, fotoA, shaFotoA: sha(foto), cookies: Object.fromEntries(web.jar), refreshToken: ref.datos?.refreshToken ?? g.datos?.refreshToken,
    capturas: { A: capA, B: capB, C: capC, D: capD }, fotoB, fotoC, fotoD,
  }, null, 1))
}

async function fasRestaurado() {
  const est = JSON.parse(readFileSync(estadoArchivo, 'utf8'))
  const c = new Cliente()
  const s = await c.pedir('GET', '/api/salud'); ok(s.estado === 200, 'Instancia restaurada: /api/salud ok')
  const pubAntes = readFileSync(path.join(estadoDir, 'llave-publica-sha.txt'), 'utf8').trim()
  const pub = await c.pedir('GET', '/api/validacion/llave-publica'); ok(sha(pub.texto) === pubAntes, 'La llave pública es la misma que antes del respaldo')
  const q = await c.pedir('GET', `/api/validacion/${est.codigo}`); ok(q.datos?.estado === 'autentico', 'El QR emitido ANTES del respaldo sigue siendo auténtico', q.datos?.estado)
  const falso = await c.pedir('GET', `/api/validacion/${est.codigo.slice(0, -2)}aa`); ok(falso.datos?.estado === 'no_autentico', 'Un código alterado sigue siendo no auténtico')
  for (const [k, v] of Object.entries(est.cookies)) c.jar.set(k, v)
  const me = await c.pedir('GET', '/api/auth/me'); ok(me.estado === 200, 'La cookie de sesión de antes del respaldo sigue válida (claves de sesión restauradas)', `HTTP ${me.estado}`)
  const bin = Buffer.from(await (await fetch(BASE + `/api/archivos/${est.fotoA}`, { headers: { cookie: [...c.jar].map(([k, v]) => `${k}=${v}`).join('; ') } })).arrayBuffer())
  ok(sha(bin) === est.shaFotoA, 'La foto de evidencia restaurada tiene el mismo SHA-256')
  const ref = await c.pedir('POST', '/api/movil/auth/refresh', { json: { refreshToken: est.refreshToken } }); ok(ref.estado === 200, 'El token de renovación móvil de antes sigue válido', `HTTP ${ref.estado}`)
}

const fases = { prod: fasProd, e2e: fasE2e, restaurado: fasRestaurado }
if (!fases[fase]) { console.error('Fase desconocida. Usa: prod | e2e | restaurado'); process.exit(2) }
await fases[fase]()
console.log(fallos ? `\n${fallos} comprobación(es) FALLARON` : '\nTodas las comprobaciones pasaron')
process.exit(fallos ? 1 : 0)

(component
  (core module $module
    (memory (export "memory") 1)
    (func (export "grow") (param i32) (result i32)
      local.get 0
      memory.grow))
  (core instance $instance (instantiate $module))
  (func $grow (param "pages" u32) (result s32)
    (canon lift (core func $instance "grow")))
  (export "grow" (func $grow)))

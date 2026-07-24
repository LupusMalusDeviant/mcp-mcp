(component
  (core module $module
    (func (export "run") (param i32) (result i32)
      local.get 0
      i32.const 1
      i32.add))
  (core instance $instance (instantiate $module))
  (func $run (param "value" s32) (result s32)
    (canon lift (core func $instance "run")))
  (export "run" (func $run)))
